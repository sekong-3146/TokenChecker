using System.Diagnostics;
using TokenChecker.Core;

namespace TokenChecker.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string CodexUsageUrl = "https://chatgpt.com/codex/settings/usage";
    private const string ClaudeUsageUrl = "https://claude.ai/settings/usage";

    private readonly Control uiDispatcher;
    private readonly SettingsStore settingsStore;
    private readonly UsageCache usageCache;
    private readonly SafeLogger logger;
    private readonly AgentLoginService loginService;
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip contextMenu;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim resourceGate = new(1, 1);
    // One settings write at a time, so a slow save cannot land after a newer one and undo it.
    private readonly SemaphoreSlim settingsSaveGate = new(1, 1);
    // UI calls still queued at exit would never run; they are cancelled so their awaiters can unwind.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TaskCompletionSource<object?>, byte> pendingUiCalls = new();
    private int queuedPopupSaves;
    private AppSettings settings = new();
    private UsageRefreshCoordinator? coordinator;
    private HttpClient? httpClient;
    private UsagePopupForm? popup;
    private readonly List<TaskbarOverlayForm> overlays = new();
    private SettingsForm? settingsForm;
    private bool initialized;
    private bool closing;
    private int refreshInProgress;

    public TrayApplicationContext()
    {
        settingsStore = new SettingsStore();
        usageCache = new UsageCache();
        logger = new SafeLogger();
        loginService = new AgentLoginService();

        uiDispatcher = new Control();
        _ = uiDispatcher.Handle;

        refreshItem = new ToolStripMenuItem("지금 새로고침")
        {
            Enabled = false
        };
        refreshItem.Click += (_, _) => _ = RefreshAsync(manual: true);

        var codexUsageItem = new ToolStripMenuItem("ChatGPT 사용량 페이지 열기");
        codexUsageItem.Click += (_, _) => OpenUrl(CodexUsageUrl);
        var claudeUsageItem = new ToolStripMenuItem("Claude 사용량 페이지 열기");
        claudeUsageItem.Click += (_, _) => OpenUrl(ClaudeUsageUrl);

        startupItem = new ToolStripMenuItem("Windows 시작 시 실행")
        {
            CheckOnClick = true,
            Enabled = false
        };
        startupItem.Click += (_, _) => _ = ToggleStartupAsync();

        var settingsItem = new ToolStripMenuItem("설정");
        settingsItem.Click += (_, _) => ShowSettings();
        var aboutItem = new ToolStripMenuItem("정보");
        aboutItem.Click += (_, _) => ShowAbout();
        var exitItem = new ToolStripMenuItem("종료");
        exitItem.Click += (_, _) => ExitThread();

        contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(refreshItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(codexUsageItem);
        contextMenu.Items.Add(claudeUsageItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(startupItem);
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(aboutItem);
        contextMenu.Items.Add(exitItem);

        notifyIcon = new NotifyIcon
        {
            Icon = TrayIconRenderer.GetIcon(-1, settings.WarnThresholdPercent, settings.CriticalThresholdPercent, TrayIconRenderer.GetCurrentDpi()),
            Text = "TokenChecker: 사용량을 불러오는 중",
            Visible = true,
            ContextMenuStrip = contextMenu
        };
        notifyIcon.MouseClick += NotifyIcon_MouseClick;

        refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 5 * 60 * 1000,
            Enabled = false
        };
        refreshTimer.Tick += (_, _) => _ = RefreshAsync(manual: false);
        // A static event: unsubscribed again on exit.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var loadedSettings = await Task.Run(
                () => settingsStore.Load(),
                lifetimeCancellation.Token).ConfigureAwait(false);

            var startupEnabled = loadedSettings.StartWithWindows;
            Exception? startupReadError = null;
            try
            {
                startupEnabled = await Task.Run(
                    StartupRegistrar.IsEnabled,
                    lifetimeCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                startupReadError = exception;
            }

            loadedSettings.StartWithWindows = startupEnabled;
            var resources = await Task.Run(
                () => BuildResources(loadedSettings, startupEnabled),
                lifetimeCancellation.Token).ConfigureAwait(false);
            if (closing || lifetimeCancellation.IsCancellationRequested || !PostToUi(() => InitializeOnUi(resources, startupReadError)))
            {
                DisposeResources(resources);
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PostToUi(() => ShowError("TokenChecker를 초기화하지 못했습니다."));
        }
    }

    private InitializationResources BuildResources(AppSettings loadedSettings, bool startupEnabled)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        try
        {
            var providers = loadedSettings.GetAgents()
                .Select<AgentAccount, IUsageProvider>(account => account.Kind switch
                {
                    AgentKind.Claude => new ClaudeUsageProvider(client, account: account),
                    _ => new CodexUsageProvider(loadedSettings.CodexExecutablePath, account: account)
                })
                .ToArray();
            var usageCoordinator = new UsageRefreshCoordinator(
                providers,
                usageCache,
                logger);
            return new InitializationResources(loadedSettings, startupEnabled, usageCoordinator, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void InitializeOnUi(InitializationResources resources, Exception? startupReadError)
    {
        if (closing)
        {
            DisposeResources(resources);
            return;
        }

        settings = resources.Settings;
        coordinator = resources.Coordinator;
        ApplyAccountEnabled(coordinator, settings);
        coordinator.SnapshotUpdated += HandleCoordinatorSnapshotUpdated;
        httpClient = resources.HttpClient;
        initialized = true;
        refreshItem.Enabled = true;
        startupItem.Enabled = true;
        startupItem.Checked = resources.StartupEnabled;
        refreshTimer.Interval = GetRefreshIntervalMilliseconds(settings.RefreshIntervalMinutes);

        PublishSnapshots(coordinator.Current);
        ApplyTaskbarOverlay();
        refreshTimer.Start();
        if (settings.PopupPinned)
        {
            // A pinned popup is meant to stay on screen, so bring it back at startup.
            TogglePopup();
        }

        if (startupReadError is not null)
        {
            ShowError("Windows 시작 설정을 읽지 못했습니다. 설정에서 다시 시도해 주세요.");
        }

        _ = RefreshAsync(manual: false);
    }

    private async Task RefreshAsync(bool manual)
    {
        if (!initialized || closing)
        {
            return;
        }

        if (Interlocked.Exchange(ref refreshInProgress, 1) != 0)
        {
            return;
        }

        var gateHeld = false;
        try
        {
            await resourceGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            gateHeld = true;
            var activeCoordinator = coordinator;
            if (closing || activeCoordinator is null)
            {
                return;
            }

            PostToUi(() =>
            {
                if (!closing)
                {
                    refreshItem.Enabled = false;
                    refreshItem.Text = "갱신 중...";
                }
            });

            var snapshots = await activeCoordinator.RefreshAsync(
                manual,
                lifetimeCancellation.Token).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (!closing && ReferenceEquals(coordinator, activeCoordinator))
                {
                    PublishSnapshots(snapshots);
                }
            });
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PostToUi(() =>
            {
                if (!closing)
                {
                    refreshItem.Text = "지금 새로고침";
                }
            });
        }
        finally
        {
            if (gateHeld)
            {
                resourceGate.Release();
            }
            Interlocked.Exchange(ref refreshInProgress, 0);
            PostToUi(() =>
            {
                if (!closing)
                {
                    refreshItem.Enabled = initialized;
                    refreshItem.Text = "지금 새로고침";
                }
            });
        }
    }
    private void HandleCoordinatorSnapshotUpdated(object? sender, UsageSnapshot snapshot)
    {
        var activeCoordinator = coordinator;
        if (closing || activeCoordinator is null || !ReferenceEquals(sender, activeCoordinator))
        {
            return;
        }

        _ = InvokeOnUiAsync(() =>
        {
            if (!closing && ReferenceEquals(coordinator, activeCoordinator))
            {
                PublishSnapshots(activeCoordinator.Current);
            }
        });
    }


    private void PublishSnapshots(IReadOnlyList<UsageSnapshot> snapshots)
    {
        if (closing)
        {
            return;
        }

        var tooltip = UsageDisplayFormatter.FormatTooltip(snapshots, settings.DisplayMode);
        SetNotifyIconText(string.IsNullOrWhiteSpace(tooltip) ? "모니터링 비활성화" : tooltip);

        var maximum = UsageDisplayFormatter.MaxUsedPercent(snapshots);
        notifyIcon.Icon = TrayIconRenderer.GetIcon(
            maximum,
            settings.WarnThresholdPercent,
            settings.CriticalThresholdPercent,
            TrayIconRenderer.GetCurrentDpi());

        if (popup is not null && !popup.IsDisposed)
        {
            popup.UpdateSnapshots(snapshots, settings.DisplayMode);
        }

        foreach (var overlay in overlays)
        {
            overlay.UpdateSnapshots(
                snapshots,
                settings.DisplayMode,
                settings.WarnThresholdPercent,
                settings.CriticalThresholdPercent);
        }
    }

    // Creates or removes taskbar overlays so there is one per monitor the saved setting asks for.
    private void ApplyTaskbarOverlay()
    {
        if (closing)
        {
            return;
        }

        var targets = settings.TaskbarOverlayEnabled
            ? TaskbarOverlayTargets.Resolve(
                settings.TaskbarOverlayScreen,
                Screen.AllScreens.Select(screen => new DisplayScreen(screen.DeviceName, screen.Primary)).ToArray())
            : Array.Empty<string>();
        foreach (var stale in overlays
                     .Where(overlay => !targets.Contains(overlay.ScreenDeviceName, StringComparer.OrdinalIgnoreCase))
                     .ToArray())
        {
            overlays.Remove(stale);
            stale.Close();
            stale.Dispose();
        }

        foreach (var target in targets)
        {
            if (overlays.Any(overlay => string.Equals(overlay.ScreenDeviceName, target, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var created = new TaskbarOverlayForm(target);
            created.LeftClicked += (_, _) => TogglePopup();
            created.RightClicked += (sender, _) => ShowOverlayMenu(sender as Form);
            created.Start();
            overlays.Add(created);
        }

        foreach (var overlay in overlays)
        {
            overlay.UpdateSnapshots(
                coordinator?.Current ?? Array.Empty<UsageSnapshot>(),
                settings.DisplayMode,
                settings.WarnThresholdPercent,
                settings.CriticalThresholdPercent);
        }
    }

    // A monitor was connected, removed or made primary: the set of taskbars to cover may have changed.
    private void HandleDisplaySettingsChanged(object? sender, EventArgs e) => PostToUi(ApplyTaskbarOverlay);

    private void ShowOverlayMenu(Form? source)
    {
        if (closing || source is null || source.IsDisposed)
        {
            return;
        }

        // Like the tray icon's menu: the app must be in the foreground, or a click elsewhere would not close it.
        SetForegroundWindow(source.Handle);
        contextMenu.Show(Cursor.Position);
    }

    // Called on the UI thread; the gate wait resumes there because the caller's context is kept.
    private async Task SaveSettingsAsync(AppSettings updatedSettings)
    {
        await settingsSaveGate.WaitAsync(lifetimeCancellation.Token);
        try
        {
            await SaveSettingsCoreAsync(updatedSettings).ConfigureAwait(false);
        }
        finally
        {
            settingsSaveGate.Release();
        }
    }

    private async Task SaveSettingsCoreAsync(AppSettings updatedSettings)
    {
        var savedSettings = updatedSettings.Clone();
        CarryPopupState(settings, savedSettings);
        var previousStartup = settings.StartWithWindows;
        var rebuildRequired = RequiresResourceRebuild(settings, savedSettings);
        InitializationResources? replacementResources = null;
        if (rebuildRequired)
        {
            replacementResources = await Task.Run(
                () => BuildResources(savedSettings, savedSettings.StartWithWindows),
                lifetimeCancellation.Token).ConfigureAwait(false);
        }

        if (!rebuildRequired || replacementResources is null)
        {
            await Task.Run(
                () => SaveSettingsAndStartup(savedSettings, previousStartup),
                lifetimeCancellation.Token).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                if (closing)
                {
                    return;
                }

                if (coordinator is not null)
                {
                    ApplyChangedAccountEnabled(coordinator, settings, savedSettings);
                }
                // Again here: the preview popup may have been resized or pinned while the save ran.
                CarryPopupState(settings, savedSettings);
                settings = savedSettings;
                startupItem.Checked = settings.StartWithWindows;
                refreshTimer.Interval = GetRefreshIntervalMilliseconds(settings.RefreshIntervalMinutes);
                ApplyPopupScreen();
                ApplyTaskbarOverlay();
                PublishSnapshots(coordinator?.Current ?? Array.Empty<UsageSnapshot>());
                _ = RefreshAsync(manual: true);
            }).ConfigureAwait(false);
            return;
        }

        UsageRefreshCoordinator? previousCoordinator = null;
        HttpClient? previousClient = null;
        var swapped = false;
        var gateHeld = false;
        try
        {
            await resourceGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            gateHeld = true;
            await InvokeOnUiAsync(() =>
            {
                previousCoordinator = coordinator;
                previousClient = httpClient;
            }).ConfigureAwait(false);
            lifetimeCancellation.Token.ThrowIfCancellationRequested();
            if (previousCoordinator is not null)
            {
                replacementResources.Coordinator.InheritRefreshState(previousCoordinator);
            }
            ApplyAccountEnabled(replacementResources.Coordinator, savedSettings);
            await Task.Run(
                () => SaveSettingsAndStartup(savedSettings, previousStartup),
                lifetimeCancellation.Token).ConfigureAwait(false);

            await InvokeOnUiAsync(() =>
            {
                if (closing)
                {
                    return;
                }

                replacementResources.Coordinator.SnapshotUpdated += HandleCoordinatorSnapshotUpdated;
                coordinator = replacementResources.Coordinator;
                httpClient = replacementResources.HttpClient;
                CarryPopupState(settings, savedSettings);
                settings = savedSettings;
                startupItem.Checked = settings.StartWithWindows;
                refreshTimer.Interval = GetRefreshIntervalMilliseconds(settings.RefreshIntervalMinutes);
                ApplyPopupScreen();
                ApplyTaskbarOverlay();
                previousCoordinator?.SnapshotUpdated -= HandleCoordinatorSnapshotUpdated;
                // Accounts removed by this save must not leave kept tokens behind.
                ClaudeUsageProvider.ForgetPendingTokensExcept(savedSettings.GetAgents()
                    .Where(account => account.Kind == AgentKind.Claude)
                    .Select(ClaudeUsageProvider.GetCredentialPath));
                swapped = true;
            }).ConfigureAwait(false);
        }
        catch
        {
            if (!swapped)
            {
                DisposeResources(replacementResources);
            }

            throw;
        }
        finally
        {
            if (gateHeld)
            {
                resourceGate.Release();
            }
        }

        if (!swapped)
        {
            DisposeResources(replacementResources);
            return;
        }

        await Task.Run(() => DisposeResources(previousCoordinator, previousClient)).ConfigureAwait(false);
        await InvokeOnUiAsync(() =>
        {
            if (!closing && ReferenceEquals(coordinator, replacementResources.Coordinator))
            {
                PublishSnapshots(replacementResources.Coordinator.Current);
            }
        }).ConfigureAwait(false);
        _ = RefreshAsync(manual: true);
    }

    // The settings dialog does not carry the popup size or pin state, so the current ones are kept.
    private static void CarryPopupState(AppSettings current, AppSettings target)
    {
        target.PopupWidth = current.PopupWidth;
        target.PopupHeight = current.PopupHeight;
        target.PopupPinned = current.PopupPinned;
    }

    private static void SetStartupRegistration(bool enabled)
    {
        if (enabled)
        {
            StartupRegistrar.Enable();
        }
        else
        {
            StartupRegistrar.Disable();
        }
    }

    private async Task ToggleStartupAsync()
    {
        if (!initialized || closing)
        {
            return;
        }

        var enabled = startupItem.Checked;
        // Set once the real registration has been read, so a failure restores what Windows actually had.
        bool? registeredBefore = null;
        startupItem.Enabled = false;
        var gateHeld = false;
        try
        {
            await settingsSaveGate.WaitAsync(lifetimeCancellation.Token);
            gateHeld = true;
            var savedSettings = settings.Clone();
            savedSettings.StartWithWindows = enabled;
            await Task.Run(
                () =>
                {
                    var registered = StartupRegistrar.IsEnabled();
                    registeredBefore = registered;
                    SetStartupRegistration(enabled);
                    try
                    {
                        settingsStore.Save(savedSettings);
                    }
                    catch (Exception saveException)
                    {
                        // The menu will show the old state again, so Windows must match it.
                        try
                        {
                            SetStartupRegistration(registered);
                        }
                        catch (Exception rollbackException)
                        {
                            throw new AggregateException(
                                "설정 저장과 Windows 시작 설정 복원에 실패했습니다.",
                                saveException,
                                rollbackException);
                        }

                        throw;
                    }
                },
                lifetimeCancellation.Token).ConfigureAwait(false);

            await InvokeOnUiAsync(() =>
            {
                if (!closing)
                {
                    // Only this field: replacing the object would undo popup changes made meanwhile.
                    settings.StartWithWindows = enabled;
                    startupItem.Checked = enabled;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await InvokeOnUiAsync(() =>
            {
                if (!closing)
                {
                    startupItem.Checked = registeredBefore ?? settings.StartWithWindows;
                    ShowError("Windows 시작 설정을 변경하지 못했습니다.");
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (gateHeld)
            {
                settingsSaveGate.Release();
            }

            await InvokeOnUiAsync(() =>
            {
                if (!closing)
                {
                    startupItem.Enabled = true;
                }
            }).ConfigureAwait(false);
        }
    }

    private void ShowSettings()
    {
        if (!initialized || closing)
        {
            return;
        }

        if (settingsForm is not null && !settingsForm.IsDisposed)
        {
            settingsForm.Activate();
            return;
        }

        popup?.Hide();
        var form = new SettingsForm(
            settings.Clone(),
            startupItem.Checked,
            SaveSettingsAsync,
            LoginAgentAsync,
            PreviewPopupTransparency);
        settingsForm = form;
        try
        {
            form.ShowDialog();
        }
        finally
        {
            form.Dispose();
            if (ReferenceEquals(settingsForm, form))
            {
                settingsForm = null;
            }

            EndPopupPreview();
        }
    }

    // Shows the popup with an unsaved transparency value while the settings dialog is open.
    private void PreviewPopupTransparency(int percent)
    {
        if (!initialized || coordinator is null || closing)
        {
            return;
        }

        if (popup is null || popup.IsDisposed)
        {
            popup = CreatePopup(coordinator);
        }
        else if (!popup.Visible)
        {
            popup.UpdateSnapshots(coordinator.Current, settings.DisplayMode);
        }

        popup.SetTransparency(percent);
        popup.ShowPreview();
    }

    // Restores the saved appearance after the settings dialog closed, whether or not it was saved.
    private void EndPopupPreview()
    {
        if (closing || popup is null || popup.IsDisposed)
        {
            return;
        }

        popup.EndPreview();
        popup.TargetScreenDeviceName = settings.PopupScreen;
        popup.SetTransparency(settings.PopupTransparencyPercent);
        if (settings.PopupPinned)
        {
            popup.ShowAtWorkAreaCorner();
        }
        else
        {
            popup.Hide();
        }
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            "TokenChecker" + Environment.NewLine + "버전 1.0.1" + Environment.NewLine + "ChatGPT / Codex와 Claude 사용량을 확인합니다.",
            "정보",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            TogglePopup();
        }
    }

    private void TogglePopup()
    {
        if (!initialized || coordinator is null || closing)
        {
            return;
        }

        if (popup is not null && !popup.IsDisposed)
        {
            if (popup.Visible)
            {
                popup.Hide();
            }
            else
            {
                popup.UpdateSnapshots(coordinator.Current, settings.DisplayMode);
                popup.ShowAtWorkAreaCorner();
            }

            return;
        }

        popup = CreatePopup(coordinator);
        popup.ShowAtWorkAreaCorner();
    }

    private UsagePopupForm CreatePopup(UsageRefreshCoordinator source)
    {
        var created = new UsagePopupForm(
            source.Current,
            settings.DisplayMode,
            () => RefreshAsync(manual: true),
            settings is { PopupWidth: { } width, PopupHeight: { } height } ? new Size(width, height) : null,
            size =>
            {
                settings.PopupWidth = size.Width;
                settings.PopupHeight = size.Height;
                _ = SavePopupStateAsync("창 크기를 저장하지 못했습니다.");
            },
            settings.PopupPinned,
            pinned =>
            {
                settings.PopupPinned = pinned;
                _ = SavePopupStateAsync("창 고정 상태를 저장하지 못했습니다.");
            })
        {
            TargetScreenDeviceName = settings.PopupScreen
        };
        created.SetTransparency(settings.PopupTransparencyPercent);
        return created;
    }

    // Applies the monitor and transparency settings to an existing popup.
    private void ApplyPopupScreen()
    {
        if (popup is null || popup.IsDisposed)
        {
            return;
        }

        popup.TargetScreenDeviceName = settings.PopupScreen;
        popup.SetTransparency(settings.PopupTransparencyPercent);
        // While the settings dialog is open, EndPopupPreview repositions the popup after it closes.
        if (popup.Visible && settingsForm is null)
        {
            popup.ShowAtWorkAreaCorner();
        }
    }

    // Called on the UI thread after the popup changed its size or pin state in `settings`.
    private async Task SavePopupStateAsync(string failureMessage)
    {
        if (closing)
        {
            return;
        }

        Interlocked.Increment(ref queuedPopupSaves);
        var gateHeld = false;
        try
        {
            await settingsSaveGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            gateHeld = true;
            // Cloned after the wait, so a queued save always writes the newest settings.
            AppSettings? savedSettings = null;
            await InvokeOnUiAsync(() => savedSettings = settings.Clone()).ConfigureAwait(false);
            if (savedSettings is null)
            {
                return;
            }

            await Task.Run(
                () => settingsStore.Save(savedSettings),
                lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PostToUi(() => ShowError(failureMessage));
        }
        finally
        {
            Interlocked.Decrement(ref queuedPopupSaves);
            if (gateHeld)
            {
                settingsSaveGate.Release();
            }
        }
    }

    // A popup change made just before exit may still be queued; write it now, on the UI thread.
    private void FlushPopupStateBeforeExit()
    {
        if (!initialized || Volatile.Read(ref queuedPopupSaves) == 0)
        {
            return;
        }

        var gateHeld = false;
        try
        {
            // Brief wait so a write already in progress lands first. If another save still owns the gate
            // (it is waiting for this thread), writing now could put older settings over its result, so skip.
            gateHeld = settingsSaveGate.Wait(TimeSpan.FromMilliseconds(500));
            if (gateHeld)
            {
                settingsStore.Save(settings.Clone());
            }
        }
        catch (Exception)
        {
            ShowError("종료 전에 창 상태를 저장하지 못했습니다.");
        }
        finally
        {
            if (gateHeld)
            {
                settingsSaveGate.Release();
            }
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            MessageBox.Show(
                "웹 페이지를 열지 못했습니다.",
                "TokenChecker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task<AgentLoginResult> LoginAgentAsync(
        AgentAccount account,
        string? codexExecutablePath,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        return await loginService.LoginAsync(
            account,
            codexExecutablePath,
            linkedCancellation.Token).ConfigureAwait(false);
    }

    private void SetNotifyIconText(string text)
    {
        var safeText = text.Length <= 63 ? text : text[..63];
        notifyIcon.Text = safeText;
    }

    private Task InvokeOnUiAsync(Action action)
    {
        if (closing || uiDispatcher.IsDisposed)
        {
            return Task.CompletedTask;
        }

        if (!uiDispatcher.InvokeRequired)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pendingUiCalls.TryAdd(completion, 0);
        if (closing)
        {
            // Exit started between the check above and the registration; it will not cancel this one.
            pendingUiCalls.TryRemove(completion, out _);
            return Task.CompletedTask;
        }

        try
        {
            uiDispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    pendingUiCalls.TryRemove(completion, out _);
                }
            }));
        }
        catch (Exception exception)
        {
            pendingUiCalls.TryRemove(completion, out _);
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private bool PostToUi(Action action)
    {
        if (closing || uiDispatcher.IsDisposed)
        {
            return false;
        }

        try
        {
            if (uiDispatcher.InvokeRequired)
            {
                uiDispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }

            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int GetRefreshIntervalMilliseconds(int minutes) => minutes switch
    {
        3 => 3 * 60 * 1000,
        10 => 10 * 60 * 1000,
        _ => 5 * 60 * 1000
    };

    private static void ApplyAccountEnabled(UsageRefreshCoordinator target, AppSettings source)
    {
        foreach (var account in source.GetAgents())
        {
            target.SetEnabled(account.Id, account.Enabled);
        }
    }

    private static void ApplyChangedAccountEnabled(
        UsageRefreshCoordinator target,
        AppSettings current,
        AppSettings updated)
    {
        var enabledById = current.GetAgents().ToDictionary(
            account => account.Id,
            account => account.Enabled,
            StringComparer.OrdinalIgnoreCase);
        foreach (var account in updated.GetAgents())
        {
            if (!enabledById.TryGetValue(account.Id, out var wasEnabled) || wasEnabled != account.Enabled)
            {
                target.SetEnabled(account.Id, account.Enabled);
            }
        }
    }

    private static bool RequiresResourceRebuild(AppSettings current, AppSettings updated)
    {
        if (!string.Equals(
                current.CodexExecutablePath?.Trim(),
                updated.CodexExecutablePath?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentAgents = current.GetAgents();
        var updatedAgents = updated.GetAgents();
        if (currentAgents.Count != updatedAgents.Count)
        {
            return true;
        }

        for (var index = 0; index < currentAgents.Count; index++)
        {
            var left = currentAgents[index];
            var right = updatedAgents[index];
            if (!string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) ||
                left.Kind != right.Kind ||
                !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                left.UseDefaultCredentials != right.UseDefaultCredentials)
            {
                return true;
            }
        }

        return false;
    }

    private void SaveSettingsAndStartup(AppSettings savedSettings, bool previousStartup)
    {
        var startupChanged = savedSettings.StartWithWindows != previousStartup;
        try
        {
            if (startupChanged && savedSettings.StartWithWindows)
            {
                StartupRegistrar.Enable();
            }
            else if (startupChanged)
            {
                StartupRegistrar.Disable();
            }

            settingsStore.Save(savedSettings);
        }
        catch (Exception saveException)
        {
            if (!startupChanged)
            {
                throw;
            }

            try
            {
                if (previousStartup)
                {
                    StartupRegistrar.Enable();
                }
                else
                {
                    StartupRegistrar.Disable();
                }
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "설정 저장과 Windows 시작 설정 복원에 실패했습니다.",
                    saveException,
                    rollbackException);
            }

            throw;
        }
    }

    private static void DisposeResources(InitializationResources resources)
    {
        if ((object)resources.Coordinator is IDisposable resource)
        {
            resource.Dispose();
        }

        resources.HttpClient.Dispose();
    }

    private static void DisposeResources(UsageRefreshCoordinator? usageCoordinator, HttpClient? client)
    {
        usageCoordinator?.Dispose();
        client?.Dispose();
    }

    private void ShowError(string message)
    {
        if (closing)
        {
            return;
        }

        MessageBox.Show(
            message,
            "TokenChecker",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    protected override void ExitThreadCore()
    {
        if (closing)
        {
            return;
        }

        FlushPopupStateBeforeExit();
        closing = true;
        lifetimeCancellation.Cancel();
        foreach (var pendingCall in pendingUiCalls.Keys)
        {
            pendingCall.TrySetCanceled();
        }

        pendingUiCalls.Clear();
        refreshTimer.Stop();
        refreshTimer.Dispose();

        if (popup is not null)
        {
            popup.Close();
            popup.Dispose();
            popup = null;
        }
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged;
        foreach (var overlay in overlays)
        {
            overlay.Close();
            overlay.Dispose();
        }
        overlays.Clear();
        if (settingsForm is not null)
        {
            settingsForm.Close();
            settingsForm.Dispose();
            settingsForm = null;
        }
        httpClient?.Dispose();
        var activeCoordinator = coordinator;
        if (activeCoordinator is not null)
        {
            activeCoordinator.SnapshotUpdated -= HandleCoordinatorSnapshotUpdated;
            activeCoordinator.Dispose();
            coordinator = null;
        }
        // lifetimeCancellation is left undisposed: continuations cancelled above may still read its Token.
        uiDispatcher.Dispose();
        TrayIconRenderer.Dispose();
        base.ExitThreadCore();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private sealed record InitializationResources(
        AppSettings Settings,
        bool StartupEnabled,
        UsageRefreshCoordinator Coordinator,
        HttpClient HttpClient);
}
