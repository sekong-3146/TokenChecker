using TokenChecker.Core;

namespace TokenChecker.Tray;

internal sealed class SettingsForm : Form
{
    private readonly ComboBox refreshIntervalComboBox;
    private readonly ComboBox displayModeComboBox;
    private readonly ComboBox popupScreenComboBox;
    private readonly List<string?> popupScreenDeviceNames = new();
    private readonly NumericUpDown popupTransparencyInput;
    private readonly TrackBar popupTransparencySlider;
    private readonly Action<int>? transparencyPreview;
    private bool syncingTransparency;
    private string? fittedScreenName;
    private readonly NumericUpDown warnThresholdInput;
    private readonly NumericUpDown criticalThresholdInput;
    private readonly CheckBox startWithWindowsCheckBox;
    private readonly TextBox codexPathInput;
    private readonly Button browseButton;
    private readonly ColumnStyle browseColumnStyle;
    private readonly Label agentsTitleLabel;
    private readonly FlowLayoutPanel agentRowsPanel;
    private readonly ComboBox addAgentKindComboBox;
    private readonly Button addAgentButton;
    private readonly Label agentLimitLabel;
    private Button saveButton = null!;
    private Button cancelButton = null!;
    private readonly Func<AppSettings, Task> saveChangesAsync;
    private readonly Func<AgentAccount, string?, CancellationToken, Task<AgentLoginResult>> loginAsync;
    private readonly List<AgentAccount> agents;
    private readonly Dictionary<string, string> loginMessages = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? loginCancellation;
    private bool loginInProgress;
    private bool saving;
    private bool formClosing;
    private bool closeAfterSave;

    public SettingsForm(
        AppSettings settings,
        bool startupEnabled,
        Func<AppSettings, Task> saveChangesAsync,
        Func<AgentAccount, string?, CancellationToken, Task<AgentLoginResult>> loginAsync,
        Action<int>? transparencyPreview = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.transparencyPreview = transparencyPreview;
        this.saveChangesAsync = saveChangesAsync ?? throw new ArgumentNullException(nameof(saveChangesAsync));
        this.loginAsync = loginAsync ?? throw new ArgumentNullException(nameof(loginAsync));
        agents = settings.GetAgents().Select(account => account.Clone()).ToList();

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        // CenterScreen opens on the cursor's screen, the same one FitToWorkArea measures.
        StartPosition = FormStartPosition.CenterScreen;
        FitToWorkArea();
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Text = "TokenChecker 설정";
        AccessibleName = "TokenChecker 설정";
        Name = "SettingsForm";

        var buttons = CreateButtonsPanel();
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 10,
            Padding = new Padding(Dip(14), Dip(14), Dip(14), Dip(8)),
            BackColor = SystemColors.Window,
            ForeColor = SystemColors.WindowText
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(145)));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 8; index++)
        {
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, Dip(40)));
        }
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, Dip(36)));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        refreshIntervalComboBox = CreateComboBox("RefreshIntervalComboBox", "갱신 주기");
        refreshIntervalComboBox.Items.AddRange(new object[] { "3분", "5분", "10분" });
        refreshIntervalComboBox.SelectedIndex = settings.RefreshIntervalMinutes switch
        {
            3 => 0,
            10 => 2,
            _ => 1
        };
        AddSettingRow(content, 0, "갱신 주기", refreshIntervalComboBox);

        displayModeComboBox = CreateComboBox("DisplayModeComboBox", "표시 기준");
        displayModeComboBox.Items.AddRange(new object[] { "사용량", "잔여량" });
        displayModeComboBox.SelectedIndex = settings.DisplayMode == UsageDisplayMode.Remaining ? 1 : 0;
        AddSettingRow(content, 1, "표시 기준", displayModeComboBox);

        popupScreenComboBox = CreateComboBox("PopupScreenComboBox", "표시 모니터");
        PopulatePopupScreens(settings.PopupScreen);
        AddSettingRow(content, 2, "표시 모니터", popupScreenComboBox);

        popupTransparencyInput = CreatePercentInput(
            Math.Clamp(settings.PopupTransparencyPercent, 0, AppSettings.MaxPopupTransparencyPercent),
            "PopupTransparencyInput",
            "창 투명도");
        popupTransparencyInput.Maximum = AppSettings.MaxPopupTransparencyPercent;
        popupTransparencyInput.Width = Dip(60);
        popupTransparencyInput.Margin = new Padding(Dip(6), Dip(2), 0, 0);
        popupTransparencySlider = new TrackBar
        {
            Minimum = 0,
            Maximum = AppSettings.MaxPopupTransparencyPercent,
            Value = decimal.ToInt32(popupTransparencyInput.Value),
            SmallChange = 5,
            LargeChange = 10,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Width = Dip(220),
            Height = Dip(28),
            Margin = new Padding(0),
            AccessibleName = "창 투명도 슬라이더",
            Name = "PopupTransparencySlider"
        };
        popupTransparencySlider.ValueChanged += (_, _) => SyncTransparency(popupTransparencySlider.Value);
        popupTransparencyInput.ValueChanged += (_, _) => SyncTransparency(decimal.ToInt32(popupTransparencyInput.Value));
        // Focusing either control brings up the preview before the first change.
        popupTransparencySlider.Enter += (_, _) => this.transparencyPreview?.Invoke(popupTransparencySlider.Value);
        popupTransparencyInput.Enter += (_, _) => this.transparencyPreview?.Invoke(popupTransparencySlider.Value);
        var transparencyPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = SystemColors.Window
        };
        transparencyPanel.Controls.Add(popupTransparencySlider);
        transparencyPanel.Controls.Add(popupTransparencyInput);
        transparencyPanel.Controls.Add(new Label
        {
            Text = "%  (0 = 불투명)",
            AutoSize = true,
            Margin = new Padding(Dip(5), Dip(6), 0, 0)
        });
        AddSettingRow(content, 3, "창 투명도", transparencyPanel);

        warnThresholdInput = CreatePercentInput(settings.WarnThresholdPercent, "WarnThresholdInput", "경고 임계값");
        AddSettingRow(content, 4, "경고 임계값", warnThresholdInput, "%");

        criticalThresholdInput = CreatePercentInput(settings.CriticalThresholdPercent, "CriticalThresholdInput", "위험 임계값");
        AddSettingRow(content, 5, "위험 임계값", criticalThresholdInput, "%");

        startWithWindowsCheckBox = new CheckBox
        {
            Text = "Windows 시작 시 실행",
            AutoSize = true,
            Checked = startupEnabled,
            Anchor = AnchorStyles.Left,
            AccessibleName = "Windows 시작 시 실행",
            Name = "StartWithWindowsCheckBox"
        };
        content.Controls.Add(startWithWindowsCheckBox, 0, 6);
        content.SetColumnSpan(startWithWindowsCheckBox, 2);

        codexPathInput = new TextBox
        {
            Text = settings.CodexExecutablePath ?? string.Empty,
            Dock = DockStyle.Fill,
            MaxLength = 1024,
            AccessibleName = "Codex 실행 파일 경로",
            Name = "CodexPathInput"
        };
        var codexPathPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = SystemColors.Window
        };
        codexPathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        browseColumnStyle = new ColumnStyle(SizeType.Absolute, Dip(70));
        codexPathPanel.ColumnStyles.Add(browseColumnStyle);
        codexPathPanel.Controls.Add(codexPathInput, 0, 0);
        browseButton = new Button
        {
            Text = "찾기...",
            Dock = DockStyle.Fill,
            Margin = new Padding(Dip(5), 0, 0, 0),
            AccessibleName = "Codex 실행 파일 찾기",
            Name = "CodexBrowseButton"
        };
        browseButton.Click += BrowseButton_Click;
        codexPathPanel.Controls.Add(browseButton, 1, 0);
        AddSettingRow(content, 7, "Codex 경로", codexPathPanel);

        agentsTitleLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
            AccessibleName = "확인할 에이전트 수",
            Name = "AgentsTitleLabel"
        };
        content.Controls.Add(agentsTitleLabel, 0, 8);
        content.SetColumnSpan(agentsTitleLabel, 2);

        var agentsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            AccessibleName = "확인할 에이전트 관리",
            Name = "AgentsPanel"
        };
        agentsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        agentsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Dip(42)));
        agentRowsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(Dip(5)),
            AccessibleName = "에이전트 목록",
            Name = "AgentRowsPanel"
        };
        agentsPanel.Controls.Add(agentRowsPanel, 0, 0);

        var addPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, Dip(5), 0, 0)
        };
        addPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(125)));
        addPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(78)));
        addPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addAgentKindComboBox = CreateAgentKindComboBox("AddAgentKindComboBox", "추가할 에이전트 종류");
        addAgentKindComboBox.SelectedIndex = 0;
        addPanel.Controls.Add(addAgentKindComboBox, 0, 0);
        addAgentButton = new Button
        {
            Text = "추가",
            Dock = DockStyle.Fill,
            Margin = new Padding(Dip(5), 0, 0, 0),
            AccessibleName = "에이전트 추가",
            Name = "AddAgentButton"
        };
        addAgentButton.Click += AddAgentButton_Click;
        addPanel.Controls.Add(addAgentButton, 1, 0);
        agentLimitLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(Dip(8), 0, 0, 0),
            ForeColor = SystemColors.GrayText,
            AccessibleName = "에이전트 추가 안내",
            Name = "AgentLimitLabel"
        };
        addPanel.Controls.Add(agentLimitLabel, 2, 0);
        agentsPanel.Controls.Add(addPanel, 0, 1);
        content.Controls.Add(agentsPanel, 0, 9);
        content.SetColumnSpan(agentsPanel, 2);

        Controls.Add(content);
        Controls.Add(buttons);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        cancelButton.DialogResult = DialogResult.None;
        FormClosing += SettingsForm_FormClosing;
        RebuildAgentRows();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // WinForms rescales a table's last absolute column from its current width, not its style,
        // which inflated these columns on a real 150% monitor; set them back to exact values.
        browseColumnStyle.Width = Dip(70);
        RebuildAgentRows();
        FitToWorkArea();
    }

    // Dragging the dialog to a smaller monitor with the same scale raises no DPI event, so check after a move.
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!string.Equals(Screen.FromControl(this).DeviceName, fittedScreenName, StringComparison.OrdinalIgnoreCase))
        {
            FitToWorkArea();
        }
    }

    // On a short work area the agent list (which scrolls) gives up height so the buttons stay reachable.
    private void FitToWorkArea()
    {
        var screen = IsHandleCreated ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position);
        fittedScreenName = screen.DeviceName;
        // Windows clamps an oversized window, after which ClientSize no longer matches the real size,
        // so the target height is computed up front instead of measured after an oversized attempt.
        var chromeHeight = SizeFromClientSize(Size.Empty).Height;
        var clientHeight = Math.Clamp(screen.WorkingArea.Height - chromeHeight, Dip(480), Dip(730));
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        ClientSize = new Size(Dip(620), clientHeight);

        MinimumSize = Size;
        MaximumSize = Size;
        // A dialog that was dragged low on the screen could still hang over the bottom edge.
        if (IsHandleCreated && Bottom > screen.WorkingArea.Bottom)
        {
            Top = Math.Max(screen.WorkingArea.Top, screen.WorkingArea.Bottom - Height);
        }
    }

    // Keeps the slider and the number box equal and previews the value once per change.
    private void SyncTransparency(int value)
    {
        if (syncingTransparency)
        {
            return;
        }

        syncingTransparency = true;
        try
        {
            popupTransparencySlider.Value = value;
            popupTransparencyInput.Value = value;
        }
        finally
        {
            syncingTransparency = false;
        }

        transparencyPreview?.Invoke(value);
    }

    private void PopulatePopupScreens(string? selectedDeviceName)
    {
        popupScreenDeviceNames.Add(null);
        popupScreenComboBox.Items.Add("자동 (클릭한 화면)");
        var screens = Screen.AllScreens;
        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            // Device names look like \\.\DISPLAYn; n is shown so the entries can be told apart.
            var digits = new string(screen.DeviceName.Where(char.IsAsciiDigit).ToArray());
            var number = digits.Length > 0 ? digits : (index + 1).ToString();
            var primary = screen.Primary ? ", 주 모니터" : string.Empty;
            popupScreenDeviceNames.Add(screen.DeviceName);
            popupScreenComboBox.Items.Add(
                $"모니터 {number} ({screen.Bounds.Width}x{screen.Bounds.Height}{primary})");
        }

        var selectedIndex = string.IsNullOrWhiteSpace(selectedDeviceName)
            ? 0
            : popupScreenDeviceNames.FindIndex(name =>
                string.Equals(name, selectedDeviceName, StringComparison.OrdinalIgnoreCase));
        if (selectedIndex < 0)
        {
            // Keep a disconnected monitor selectable so saving does not silently drop it.
            popupScreenDeviceNames.Add(selectedDeviceName);
            popupScreenComboBox.Items.Add($"{selectedDeviceName!.TrimStart('\\', '.')} (연결 안 됨)");
            selectedIndex = popupScreenDeviceNames.Count - 1;
        }

        popupScreenComboBox.SelectedIndex = selectedIndex;
    }

    private FlowLayoutPanel CreateButtonsPanel()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Dip(46),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(Dip(8), Dip(4), Dip(14), Dip(8)),
            BackColor = SystemColors.Window
        };
        saveButton = new Button
        {
            Text = "저장",
            AutoSize = true,
            MinimumSize = new Size(Dip(78), Dip(29)),
            DialogResult = DialogResult.None,
            AccessibleName = "설정 저장",
            Name = "SaveButton"
        };
        saveButton.Click += SaveButton_Click;
        cancelButton = new Button
        {
            Text = "취소",
            AutoSize = true,
            MinimumSize = new Size(Dip(78), Dip(29)),
            DialogResult = DialogResult.None,
            AccessibleName = "설정 취소",
            Name = "CancelButton"
        };
        cancelButton.Click += CancelButton_Click;
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        return buttons;
    }

    private void RebuildAgentRows()
    {
        foreach (Control child in agentRowsPanel.Controls.Cast<Control>().ToArray())
        {
            agentRowsPanel.Controls.Remove(child);
            child.Dispose();
        }

        for (var index = 0; index < agents.Count; index++)
        {
            agentRowsPanel.Controls.Add(CreateAgentRow(agents[index], index));
        }

        agentsTitleLabel.Text = $"확인할 에이전트 ({agents.Count}/{AppSettings.MaxAgents})";
        addAgentButton.Enabled = !loginInProgress && !saving && agents.Count < AppSettings.MaxAgents;
        addAgentKindComboBox.Enabled = !loginInProgress && !saving && agents.Count < AppSettings.MaxAgents;
        agentLimitLabel.Text = agents.Count >= AppSettings.MaxAgents
            ? "최대 4개까지 추가할 수 있습니다."
            : "Codex와 Claude를 원하는 비율로 추가할 수 있습니다.";
    }

    private Control CreateAgentRow(AgentAccount account, int index)
    {
        var row = new TableLayoutPanel
        {
            Width = Dip(558),
            Height = Dip(58),
            ColumnCount = 5,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, Dip(4)),
            AccessibleName = $"에이전트 {index + 1}",
            Name = $"AgentRow_{index}"
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(68)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(110)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(68)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(58)));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, Dip(31)));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, Dip(23)));

        var enabledCheckBox = new CheckBox
        {
            Text = "사용",
            Checked = account.Enabled,
            Dock = DockStyle.Fill,
            Enabled = !loginInProgress && !saving,
            AccessibleName = $"{account.Name} 사용 여부",
            Name = $"AgentEnabled_{index}",
            Tag = account
        };
        enabledCheckBox.CheckedChanged += AgentEnabledCheckBox_CheckedChanged;
        row.Controls.Add(enabledCheckBox, 0, 0);

        var kindComboBox = CreateAgentKindComboBox($"AgentKind_{index}", $"{account.Name} 종류");
        kindComboBox.SelectedIndex = account.Kind == AgentKind.Claude ? 1 : 0;
        kindComboBox.Enabled = !loginInProgress && !saving;
        kindComboBox.Tag = account;
        kindComboBox.SelectedIndexChanged += AgentKindComboBox_SelectedIndexChanged;
        row.Controls.Add(kindComboBox, 1, 0);

        var nameInput = new TextBox
        {
            Text = account.Name,
            Dock = DockStyle.Fill,
            MaxLength = 80,
            Enabled = !loginInProgress && !saving,
            AccessibleName = $"에이전트 {index + 1} 이름",
            Name = $"AgentName_{index}",
            Tag = account
        };
        nameInput.TextChanged += AgentNameInput_TextChanged;
        row.Controls.Add(nameInput, 2, 0);

        var loginButton = new Button
        {
            Text = "로그인",
            Dock = DockStyle.Fill,
            Enabled = !loginInProgress && !saving,
            AccessibleName = $"{account.Name} 로그인",
            Name = $"AgentLogin_{index}",
            Tag = account
        };
        loginButton.Click += LoginButton_Click;
        row.Controls.Add(loginButton, 3, 0);

        var removeButton = new Button
        {
            Text = "제거",
            Dock = DockStyle.Fill,
            Enabled = !loginInProgress && !saving,
            AccessibleName = $"{account.Name} 제거",
            Name = $"AgentRemove_{index}",
            Tag = account
        };
        removeButton.Click += RemoveButton_Click;
        row.Controls.Add(removeButton, 4, 0);

        var statusLabel = new Label
        {
            Text = loginMessages.GetValueOrDefault(account.Id, string.Empty),
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = $"{account.Name} 로그인 상태",
            Name = $"AgentLoginStatus_{index}"
        };
        row.Controls.Add(statusLabel, 1, 1);
        row.SetColumnSpan(statusLabel, 4);
        return row;
    }

    private async void LoginButton_Click(object? sender, EventArgs e)
    {
        if (loginInProgress || saving || sender is not Button { Tag: AgentAccount currentAccount })
        {
            return;
        }

        CommitAccountNames();
        var replacement = AgentAccount.Create(currentAccount.Kind);
        replacement.Name = currentAccount.Name;
        replacement.Enabled = currentAccount.Enabled;
        loginInProgress = true;
        var activeCancellation = new CancellationTokenSource();
        loginCancellation = activeCancellation;
        loginMessages[currentAccount.Id] = $"브라우저에서 {GetKindName(currentAccount.Kind)} 로그인을 완료해 주세요.";
        UpdateBusyState();
        try
        {
            var result = await loginAsync(
                replacement.Clone(),
                string.IsNullOrWhiteSpace(codexPathInput.Text) ? null : codexPathInput.Text.Trim(),
                activeCancellation.Token);
            if (!formClosing && !IsDisposed && !Disposing)
            {
                if (activeCancellation.IsCancellationRequested)
                {
                    loginMessages[currentAccount.Id] = "로그인이 취소되었습니다.";
                }
                else if (result.Success)
                {
                    var index = agents.IndexOf(currentAccount);
                    if (index >= 0)
                    {
                        agents[index] = replacement;
                        loginMessages.Remove(currentAccount.Id);
                        loginMessages[replacement.Id] = GetResultMessage(result.Message, "로그인이 완료되었습니다.");
                    }
                }
                else
                {
                    loginMessages[currentAccount.Id] = GetResultMessage(
                        result.Message,
                        "로그인에 실패했습니다. 다시 시도해 주세요.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!formClosing && !IsDisposed && !Disposing)
            {
                loginMessages[currentAccount.Id] = "로그인이 취소되었습니다.";
            }
        }
        catch (Exception)
        {
            if (!formClosing && !IsDisposed && !Disposing)
            {
                loginMessages[currentAccount.Id] = "로그인에 실패했습니다. 다시 시도해 주세요.";
            }
        }
        finally
        {
            activeCancellation.Dispose();
            if (ReferenceEquals(loginCancellation, activeCancellation))
            {
                loginCancellation = null;
            }
            loginInProgress = false;
            if (!formClosing && !IsDisposed && !Disposing)
            {
                RebuildAgentRows();
                UpdateBusyState();
            }
        }
    }

    private void UpdateBusyState()
    {
        saveButton.Enabled = !loginInProgress && !saving;
        cancelButton.Enabled = !saving;
        cancelButton.Text = loginInProgress ? "로그인 취소" : "취소";
        cancelButton.AccessibleName = loginInProgress ? "로그인 취소" : "설정 취소";
        codexPathInput.Enabled = !loginInProgress && !saving;
        browseButton.Enabled = !loginInProgress && !saving;
        refreshIntervalComboBox.Enabled = !loginInProgress && !saving;
        displayModeComboBox.Enabled = !loginInProgress && !saving;
        popupScreenComboBox.Enabled = !loginInProgress && !saving;
        popupTransparencyInput.Enabled = !loginInProgress && !saving;
        popupTransparencySlider.Enabled = !loginInProgress && !saving;
        warnThresholdInput.Enabled = !loginInProgress && !saving;
        criticalThresholdInput.Enabled = !loginInProgress && !saving;
        startWithWindowsCheckBox.Enabled = !loginInProgress && !saving;
        addAgentButton.Enabled = !loginInProgress && !saving && agents.Count < AppSettings.MaxAgents;
        addAgentKindComboBox.Enabled = !loginInProgress && !saving && agents.Count < AppSettings.MaxAgents;

        foreach (var row in agentRowsPanel.Controls.Cast<Control>())
        {
            var account = row.Controls.Cast<Control>()
                .Select(control => control.Tag)
                .OfType<AgentAccount>()
                .FirstOrDefault();
            foreach (Control control in row.Controls)
            {
                if (control is Label statusLabel)
                {
                    statusLabel.Text = account is null
                        ? string.Empty
                        : loginMessages.GetValueOrDefault(account.Id, string.Empty);
                }
                else
                {
                    control.Enabled = !loginInProgress && !saving;
                }
            }
        }
    }

    private void AddAgentButton_Click(object? sender, EventArgs e)
    {
        if (loginInProgress || saving || agents.Count >= AppSettings.MaxAgents)
        {
            return;
        }

        var account = AgentAccount.Create(GetSelectedKind(addAgentKindComboBox));
        account.Name = GetUniqueName(account.Kind);
        agents.Add(account);
        RebuildAgentRows();
    }

    private void RemoveButton_Click(object? sender, EventArgs e)
    {
        if (loginInProgress || saving || sender is not Button { Tag: AgentAccount account })
        {
            return;
        }

        agents.Remove(account);
        loginMessages.Remove(account.Id);
        RebuildAgentRows();
    }

    private void AgentKindComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (loginInProgress || saving || sender is not ComboBox { Tag: AgentAccount account } comboBox)
        {
            return;
        }

        var selectedKind = GetSelectedKind(comboBox);
        if (selectedKind == account.Kind)
        {
            return;
        }

        CommitAccountNames();
        var index = agents.IndexOf(account);
        if (index < 0)
        {
            return;
        }

        var replacement = AgentAccount.Create(selectedKind);
        replacement.Name = string.Equals(account.Name, GetKindName(account.Kind), StringComparison.OrdinalIgnoreCase)
            ? GetUniqueName(selectedKind)
            : account.Name;
        replacement.Enabled = account.Enabled;
        agents[index] = replacement;
        loginMessages.Remove(account.Id);
        RebuildAgentRows();
    }

    private static void AgentEnabledCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        if (sender is CheckBox { Tag: AgentAccount account } checkBox)
        {
            account.Enabled = checkBox.Checked;
        }
    }

    private static void AgentNameInput_TextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBox { Tag: AgentAccount account } textBox)
        {
            account.Name = textBox.Text;
        }
    }

    private void CommitAccountNames()
    {
        foreach (var textBox in agentRowsPanel.Controls
                     .Cast<Control>()
                     .SelectMany(row => row.Controls.Cast<Control>())
                     .OfType<TextBox>())
        {
            if (textBox.Tag is AgentAccount account)
            {
                account.Name = textBox.Text.Trim();
            }
        }
    }

    private void CancelButton_Click(object? sender, EventArgs e)
    {
        if (loginInProgress)
        {
            cancelButton.Enabled = false;
            loginCancellation?.Cancel();
            return;
        }

        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void SettingsForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (saving && !closeAfterSave && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        formClosing = true;
        loginCancellation?.Cancel();
    }

    private void AddSettingRow(TableLayoutPanel table, int row, string label, Control value, string? suffix = null)
    {
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        table.Controls.Add(labelControl, 0, row);

        if (suffix is null)
        {
            value.Margin = new Padding(0, Dip(5), 0, Dip(5));
            table.Controls.Add(value, 1, row);
            return;
        }

        var valuePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, Dip(5), 0, Dip(5)),
            Margin = new Padding(0)
        };
        value.Margin = new Padding(0);
        valuePanel.Controls.Add(value);
        valuePanel.Controls.Add(new Label
        {
            Text = suffix,
            AutoSize = true,
            Margin = new Padding(Dip(5), Dip(4), 0, 0)
        });
        table.Controls.Add(valuePanel, 1, row);
    }

    private ComboBox CreateComboBox(string name, string accessibleName) => new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        IntegralHeight = true,
        Name = name,
        AccessibleName = accessibleName
    };

    private ComboBox CreateAgentKindComboBox(string name, string accessibleName)
    {
        var comboBox = CreateComboBox(name, accessibleName);
        comboBox.Items.AddRange(new object[] { "Codex", "Claude" });
        return comboBox;
    }

    private NumericUpDown CreatePercentInput(int value, string name, string accessibleName) => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = Math.Clamp(value, 0, 100),
        Width = Dip(95),
        Height = Dip(26),
        TextAlign = HorizontalAlignment.Right,
        DecimalPlaces = 0,
        Name = name,
        AccessibleName = accessibleName
    };

    private async void SaveButton_Click(object? sender, EventArgs e)
    {
        var warnThreshold = decimal.ToInt32(warnThresholdInput.Value);
        var criticalThreshold = decimal.ToInt32(criticalThresholdInput.Value);
        if (criticalThreshold < warnThreshold)
        {
            MessageBox.Show(
                this,
                "위험 임계값은 경고 임계값보다 작을 수 없습니다.",
                "TokenChecker 설정",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        CommitAccountNames();
        for (var index = 0; index < agents.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(agents[index].Name))
            {
                MessageBox.Show(
                    this,
                    $"에이전트 {index + 1}의 이름을 입력해 주세요.",
                    "TokenChecker 설정",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }
        var duplicateName = agents
            .GroupBy(account => account.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            MessageBox.Show(
                this,
                $"에이전트 이름 '{duplicateName}'이(가) 중복됩니다. 서로 다른 이름을 입력해 주세요.",
                "TokenChecker 설정",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var updated = new AppSettings
        {
            RefreshIntervalMinutes = refreshIntervalComboBox.SelectedIndex switch
            {
                0 => 3,
                2 => 10,
                _ => 5
            },
            DisplayMode = displayModeComboBox.SelectedIndex == 1
                ? UsageDisplayMode.Remaining
                : UsageDisplayMode.Used,
            WarnThresholdPercent = warnThreshold,
            CriticalThresholdPercent = criticalThreshold,
            StartWithWindows = startWithWindowsCheckBox.Checked,
            PopupScreen = popupScreenComboBox.SelectedIndex >= 0
                ? popupScreenDeviceNames[popupScreenComboBox.SelectedIndex]
                : null,
            PopupTransparencyPercent = decimal.ToInt32(popupTransparencyInput.Value),
            CodexExecutablePath = string.IsNullOrWhiteSpace(codexPathInput.Text)
                ? null
                : codexPathInput.Text.Trim(),
            Agents = agents.Select(account => account.Clone()).ToList()
        };

        saving = true;
        UpdateBusyState();
        try
        {
            await saveChangesAsync(updated);
            if (IsDisposed || Disposing)
            {
                // The app exited while the save was running; there is no dialog left to close.
                return;
            }

            closeAfterSave = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            if (IsDisposed || Disposing)
            {
                // Exit cancels a save in flight; a message box cannot be owned by a disposed dialog.
                return;
            }

            MessageBox.Show(
                this,
                $"설정을 저장하지 못했습니다.\n{exception.Message}",
                "TokenChecker 설정",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            saving = false;
            if (!formClosing && !IsDisposed && !Disposing)
            {
                UpdateBusyState();
            }
        }
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Codex 실행 파일 선택",
            Filter = "실행 파일 (*.exe)|*.exe|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = codexPathInput.Text
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            codexPathInput.Text = dialog.FileName;
        }
    }

    private static AgentKind GetSelectedKind(ComboBox comboBox) =>
        comboBox.SelectedIndex == 1 ? AgentKind.Claude : AgentKind.Codex;

    private static string GetKindName(AgentKind kind) => kind == AgentKind.Claude ? "Claude" : "Codex";

    private static string GetResultMessage(string message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message;

    private string GetUniqueName(AgentKind kind)
    {
        var baseName = GetKindName(kind);
        var candidate = baseName;
        var suffix = 2;
        while (agents.Any(account => string.Equals(account.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} {suffix}";
            suffix++;
        }

        return candidate;
    }

    private int Dip(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
}
