using Microsoft.Win32;
using TokenChecker.Core;

namespace TokenChecker.Tray;

internal sealed class UsagePopupForm : Form
{
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    private readonly FlowLayoutPanel rowsPanel;
    private readonly Label footerLabel;
    // Segoe icon font code points (same in "Segoe Fluent Icons" and "Segoe MDL2 Assets").
    private const string RefreshGlyph = "";
    private const string PinGlyph = "";
    private const string UnpinGlyph = "";
    private const int ResizeGripDip = 5;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);

    private readonly Button refreshButton;
    private readonly Button pinButton;
    private readonly Font iconFont;
    // Form font size before any DPI scaling. WinForms multiplies explicitly assigned child fonts by
    // the monitor scale itself, so they must be built from this, never from the already scaled Font.
    private readonly float baseFontSize;
    private readonly Font boldFont;
    private readonly ToolTip resetToolTip;
    // Row labels are recreated on every update. A ToolTip keeps every control it was given alive,
    // so the rows get their own instance that is emptied before each rebuild.
    private readonly ToolTip rowToolTip;
    private readonly Func<Task> refreshRequested;
    private readonly Color windowBackColor;
    private readonly Color windowForeColor;
    private readonly Color secondaryForeColor;
    private readonly Color dividerColor;
    private readonly Color accentColor;
    private IReadOnlyList<UsageSnapshot> snapshots = Array.Empty<UsageSnapshot>();
    private UsageDisplayMode displayMode;
    private bool refreshInProgress;
    private bool pinned;
    private bool previewing;
    private bool resizingLeft;
    private bool resizingTop;
    private readonly Action<Size>? dipSizeChanged;
    private readonly Action<bool>? pinnedChanged;
    private Size dipSize = new(360, 280);
    private Rectangle resizeStartBounds;
    private Point resizeStartCursor;

    public UsagePopupForm(
        IReadOnlyList<UsageSnapshot> snapshots,
        UsageDisplayMode displayMode,
        Func<Task> refreshRequested,
        Size? savedDipSize = null,
        Action<Size>? dipSizeChanged = null,
        bool savedPinned = false,
        Action<bool>? pinnedChanged = null)
    {
        this.refreshRequested = refreshRequested ?? throw new ArgumentNullException(nameof(refreshRequested));
        baseFontSize = Font.SizeInPoints;
        boldFont = new Font(Font.FontFamily, baseFontSize, FontStyle.Bold);
        rowToolTip = new ToolTip();
        this.dipSizeChanged = dipSizeChanged;
        this.pinnedChanged = pinnedChanged;
        if (savedDipSize is { Width: > 0, Height: > 0 } saved)
        {
            dipSize = saved;
        }
        this.displayMode = displayMode;
        resetToolTip = new ToolTip();

        var isLightTheme = IsLightTheme();
        windowBackColor = isLightTheme ? Color.White : Color.FromArgb(32, 32, 32);
        windowForeColor = isLightTheme ? Color.FromArgb(32, 32, 32) : Color.FromArgb(242, 242, 242);
        secondaryForeColor = isLightTheme ? Color.FromArgb(95, 95, 95) : Color.FromArgb(185, 185, 185);
        dividerColor = isLightTheme ? Color.FromArgb(220, 220, 220) : Color.FromArgb(72, 72, 72);
        accentColor = isLightTheme ? Color.FromArgb(0, 102, 204) : Color.FromArgb(100, 170, 255);

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ApplyDpiDependentBounds();
        BackColor = windowBackColor;
        ForeColor = windowForeColor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        AccessibleName = "TokenChecker 사용량";
        AccessibleDescription = "공급자별 사용량과 초기화 시각";

        var footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Dip(29),
            Padding = new Padding(Dip(12), Dip(4), Dip(12), Dip(4)),
            BackColor = windowBackColor
        };
        footerLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = secondaryForeColor,
            BackColor = windowBackColor,
            AccessibleName = "마지막 갱신"
        };
        iconFont = CreateIconFont();
        refreshButton = CreateIconButton(RefreshGlyph, "새로고침");
        refreshButton.Click += RefreshButton_Click;
        pinButton = CreateIconButton(PinGlyph, "고정");
        pinButton.Click += PinButton_Click;
        if (savedPinned)
        {
            ApplyPinned(true);
        }

        // Later-added controls dock first, so the pin button sits at the far right.
        footerPanel.Controls.Add(footerLabel);
        footerPanel.Controls.Add(refreshButton);
        footerPanel.Controls.Add(pinButton);

        rowsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(Dip(12), Dip(8), Dip(8), Dip(4)),
            BackColor = windowBackColor,
            ForeColor = windowForeColor,
            AccessibleName = "공급자 사용량"
        };

        rowsPanel.SizeChanged += (_, _) => UpdateRowWidths();

        Controls.Add(rowsPanel);
        Controls.Add(footerPanel);

        UpdateSnapshots(snapshots, displayMode);
    }

    // Screen.DeviceName of the monitor to show on; null means the screen that was clicked.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? TargetScreenDeviceName { get; set; }

    // 0 is opaque; the cap keeps the popup visible even with a bad value.
    public void SetTransparency(int percent)
    {
        Opacity = (100 - Math.Clamp(percent, 0, AppSettings.MaxPopupTransparencyPercent)) / 100.0;
    }

    public void UpdateSnapshots(IReadOnlyList<UsageSnapshot> snapshots, UsageDisplayMode displayMode)
    {
        this.snapshots = snapshots ?? Array.Empty<UsageSnapshot>();
        this.displayMode = displayMode;
        RebuildRows();
        UpdateFooter();
    }

    // Shows the popup above other windows without taking focus, e.g. while a settings dialog previews it.
    public void ShowPreview()
    {
        previewing = true;
        MoveToWorkAreaCorner();
        if (!Visible)
        {
            Show();
            MoveToWorkAreaCorner(stayOnCurrentScreen: true);
        }

        // The TopMost property would activate the window, so the z-order is set natively.
        SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    public void EndPreview()
    {
        if (!previewing)
        {
            return;
        }

        previewing = false;
        if (!TopMost && IsHandleCreated)
        {
            SetWindowPos(Handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
    }

    protected override bool ShowWithoutActivation => previewing;

    public void ShowAtWorkAreaCorner()
    {
        MoveToWorkAreaCorner();
        Show();
        // Showing on a monitor with another scale resizes the window, so anchor again afterwards.
        MoveToWorkAreaCorner(stayOnCurrentScreen: true);
        Activate();
        // No initial focus: a focused icon button would draw a border and react to Enter/Space.
        ActiveControl = null;
    }

    // Aligns to the bottom-right corner of the work area (above the taskbar) on the target screen.
    // stayOnCurrentScreen: re-anchor where the popup already is instead of following the cursor.
    private void MoveToWorkAreaCorner(bool stayOnCurrentScreen = false)
    {
        // A chosen monitor that is no longer connected falls back to the clicked screen.
        var screen = Screen.AllScreens.FirstOrDefault(candidate =>
                         string.Equals(candidate.DeviceName, TargetScreenDeviceName, StringComparison.OrdinalIgnoreCase))
                     ?? (stayOnCurrentScreen ? Screen.FromControl(this) : Screen.FromPoint(Cursor.Position));
        var workArea = screen.WorkingArea;
        // A size saved on a larger screen must still fit this one.
        if (Width > workArea.Width || Height > workArea.Height)
        {
            Size = new Size(Math.Min(Width, workArea.Width), Math.Min(Height, workArea.Height));
        }

        Location = new Point(
            Math.Max(workArea.Left, workArea.Right - Width),
            Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!pinned && !previewing)
        {
            Hide();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!pinned && (keyData & Keys.KeyCode) == Keys.Escape)
        {
            Hide();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Explicit pixel values were computed for the old DPI; redo them, then fit the new screen again.
        ApplyDpiDependentBounds();
        RebuildRows();
        MoveToWorkAreaCorner(stayOnCurrentScreen: true);
    }

    private void ApplyDpiDependentBounds()
    {
        // Measured floor: one full account block (with stale notice) and a 40px+ progress bar.
        MinimumSize = new Size(Dip(300), Dip(170));
        ClientSize = new Size(Dip(dipSize.Width), Dip(dipSize.Height));
        // The uncovered left/top strip is the resize grip; the bottom-right corner stays anchored.
        Padding = new Padding(Dip(ResizeGripDip), Dip(ResizeGripDip), 0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            resetToolTip.Dispose();
            rowToolTip.Dispose();
            iconFont.Dispose();
            boldFont.Dispose();
        }

        base.Dispose(disposing);
    }

    // A borderless form gets no system sizing loop, so the left/top grip is resized by hand.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var (left, top) = GetResizeEdges(e.Location);
        if (!left && !top)
        {
            return;
        }

        resizingLeft = left;
        resizingTop = top;
        resizeStartBounds = Bounds;
        resizeStartCursor = Cursor.Position;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!resizingLeft && !resizingTop)
        {
            var (left, top) = GetResizeEdges(e.Location);
            Cursor = left && top ? Cursors.SizeNWSE
                : left ? Cursors.SizeWE
                : top ? Cursors.SizeNS
                : Cursors.Default;
            return;
        }

        // The bottom-right corner stays fixed; only the dragged edges move, and never past the work area.
        var cursor = Cursor.Position;
        var workArea = Screen.FromRectangle(resizeStartBounds).WorkingArea;
        var x = resizeStartBounds.Left;
        var y = resizeStartBounds.Top;
        if (resizingLeft)
        {
            x = Math.Min(
                Math.Max(workArea.Left, resizeStartBounds.Left + cursor.X - resizeStartCursor.X),
                resizeStartBounds.Right - MinimumSize.Width);
        }

        if (resizingTop)
        {
            y = Math.Min(
                Math.Max(workArea.Top, resizeStartBounds.Top + cursor.Y - resizeStartCursor.Y),
                resizeStartBounds.Bottom - MinimumSize.Height);
        }

        // A popup left hanging over the edge (e.g. after a display change) is pulled back inside as well:
        // the size is settled first, then the rectangle is slid into the work area.
        var right = Math.Min(resizeStartBounds.Right, workArea.Right);
        var bottom = Math.Min(resizeStartBounds.Bottom, workArea.Bottom);
        var width = Math.Min(Math.Max(MinimumSize.Width, right - x), workArea.Width);
        var height = Math.Min(Math.Max(MinimumSize.Height, bottom - y), workArea.Height);
        x = Math.Clamp(x, workArea.Left, workArea.Right - width);
        y = Math.Clamp(y, workArea.Top, workArea.Bottom - height);
        Bounds = new Rectangle(x, y, width, height);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            EndResize();
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            EndResize();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!resizingLeft && !resizingTop)
        {
            Cursor = Cursors.Default;
        }
    }

    private void EndResize()
    {
        if (!resizingLeft && !resizingTop)
        {
            return;
        }

        resizingLeft = false;
        resizingTop = false;
        Capture = false;
        if (Size == resizeStartBounds.Size)
        {
            return;
        }

        dipSize = new Size(ToDip(Width), ToDip(Height));
        dipSizeChanged?.Invoke(dipSize);
    }

    private (bool Left, bool Top) GetResizeEdges(Point point)
    {
        var grip = Dip(ResizeGripDip);
        var corner = Dip(16);
        var onLeft = point.X < grip;
        var onTop = point.Y < grip;
        var onCorner = (onLeft && point.Y < corner) || (onTop && point.X < corner);
        return (onLeft || onCorner, onTop || onCorner);
    }

    private int GetRowWidth() => Math.Max(
        Dip(220),
        rowsPanel.Width - rowsPanel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - Dip(2));

    private void UpdateRowWidths()
    {
        if (rowsPanel.IsDisposed)
        {
            return;
        }

        var width = GetRowWidth();
        rowsPanel.SuspendLayout();
        foreach (var panel in rowsPanel.Controls.OfType<Panel>())
        {
            panel.Width = width;
        }

        rowsPanel.ResumeLayout();
        // Shrinking rows does not shrink the scroll extent until the next layout pass.
        rowsPanel.PerformLayout();
    }

    private void RebuildRows()
    {
        if (rowsPanel.IsDisposed)
        {
            return;
        }

        rowToolTip.RemoveAll();
        foreach (Control child in rowsPanel.Controls.Cast<Control>().ToArray())
        {
            rowsPanel.Controls.Remove(child);
            child.Dispose();
        }

        var visibleSnapshots = snapshots.Where(snapshot => snapshot.Status != ProviderStatus.Disabled).ToArray();
        if (visibleSnapshots.Length == 0)
        {
            rowsPanel.Controls.Add(CreateMessageLabel("모니터링할 공급자가 없습니다."));
            return;
        }

        foreach (var snapshot in visibleSnapshots)
        {
            rowsPanel.Controls.Add(CreateProviderPanel(snapshot));
        }
    }

    private Control CreateProviderPanel(UsageSnapshot snapshot)
    {
        // The vertical scrollbar appears once providers overflow, so its width is always reserved.
        var width = GetRowWidth();
        var hasStaleNotice = snapshot.Status == ProviderStatus.Stale;
        var isCodexPro = UsageDisplayFormatter.IsCodexPro(snapshot);
        var usageRowsHeight = isCodexPro ? Dip(29) : Dip(58);
        // Docked children do not grow an AutoSize panel reliably, so the height is explicit.
        var panel = new Panel
        {
            Width = width,
            Height = Dip(23) + Dip(20) + usageRowsHeight + (hasStaleNotice ? Dip(18) : 0),
            BackColor = windowBackColor,
            ForeColor = windowForeColor,
            Margin = new Padding(0, 0, 0, Dip(5)),
            AccessibleName = GetProviderName(snapshot)
        };

        var titlePanel = new Panel
        {
            Width = width,
            Height = Dip(23),
            Dock = DockStyle.Top,
            BackColor = windowBackColor
        };
        // The name takes the remaining width and ellipsizes, so it never runs under the status text.
        var providerLabel = new Label
        {
            Text = GetProviderName(snapshot),
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = boldFont,
            ForeColor = windowForeColor,
            BackColor = windowBackColor
        };
        var statusLabel = new Label
        {
            Text = UsageDisplayFormatter.FormatStatus(snapshot),
            AutoEllipsis = true,
            Dock = DockStyle.Right,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = secondaryForeColor,
            BackColor = windowBackColor,
            AccessibleName = "공급자 상태"
        };
        // Measured with the form's scaled font: the label has no parent yet, so its own font is unscaled.
        statusLabel.Width = Math.Max(
            Dip(100),
            TextRenderer.MeasureText(statusLabel.Text, Font).Width + Dip(6));
        titlePanel.Controls.Add(providerLabel);
        titlePanel.Controls.Add(statusLabel);
        var planLabel = new Label
        {
            Text = $"요금제: {UsageDisplayFormatter.FormatPlan(snapshot)}",
            Name = $"PlanLabel_{snapshot.ProviderId}",
            AutoEllipsis = true,
            Width = width,
            Height = Dip(20),
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = secondaryForeColor,
            BackColor = windowBackColor,
            AccessibleName = "요금제"
        };
        Label? staleLabel = null;

        if (hasStaleNotice)
        {
            staleLabel = new Label
            {
                Text = $"! 마지막 성공 {snapshot.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                AutoSize = true,
                Height = Dip(18),
                Dock = DockStyle.Top,
                ForeColor = Color.FromArgb(190, 120, 20),
                BackColor = windowBackColor,
                AccessibleName = "오래된 사용량",
                AccessibleDescription = $"마지막 성공 {snapshot.RetrievedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            };
        }

        var rows = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = isCodexPro ? 1 : 2,
            Width = width,
            Height = usageRowsHeight,
            Dock = DockStyle.Top,
            BackColor = windowBackColor,
            ForeColor = windowForeColor,
            Margin = new Padding(0)
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(48)));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(48)));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dip(104)));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, Dip(29)));
        if (isCodexPro)
        {
            AddWindowRow(rows, 0, "주간", WeeklyWindow, snapshot);
        }
        else
        {
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, Dip(29)));
            AddWindowRow(rows, 0, "5시간", FiveHourWindow, snapshot);
            AddWindowRow(rows, 1, "주간", WeeklyWindow, snapshot);
        }

        panel.Controls.Add(rows);
        if (staleLabel is not null)
        {
            panel.Controls.Add(staleLabel);
        }

        panel.Controls.Add(planLabel);
        panel.Controls.Add(titlePanel);

        return panel;
    }

    private void AddWindowRow(
        TableLayoutPanel rows,
        int rowIndex,
        string label,
        TimeSpan duration,
        UsageSnapshot snapshot)
    {
        var window = UsageDisplayFormatter.SelectWindow(snapshot, duration);
        var usedPercent = window?.UsedPercent ?? 0;
        if (!double.IsFinite(usedPercent))
        {
            usedPercent = 0;
        }
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = secondaryForeColor,
            BackColor = windowBackColor
        };
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = (int)Math.Clamp(Math.Round(usedPercent), 0, 100),
            Style = ProgressBarStyle.Continuous,
            Dock = DockStyle.Fill,
            Margin = new Padding(Dip(2), Dip(8), Dip(5), Dip(8)),
            AccessibleName = $"{label} 사용률"
        };
        var percent = new Label
        {
            Text = window is null ? "--" : UsageDisplayFormatter.FormatPercent(window.UsedPercent, displayMode),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = windowForeColor,
            BackColor = windowBackColor,
            AccessibleName = $"{label} 비율"
        };
        var reset = new Label
        {
            Text = window is null ? "--" : UsageDisplayFormatter.FormatReset(window.ResetsAt, DateTimeOffset.Now),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            ForeColor = secondaryForeColor,
            BackColor = windowBackColor,
            AccessibleName = $"{label} 초기화 시각"
        };
        rowToolTip.SetToolTip(reset, window is null ? "--" : UsageDisplayFormatter.FormatResetAccessible(window.ResetsAt));
        reset.AccessibleDescription = window is null ? "초기화 시각 없음" : UsageDisplayFormatter.FormatResetAccessible(window.ResetsAt);

        rows.Controls.Add(labelControl, 0, rowIndex);
        rows.Controls.Add(progress, 1, rowIndex);
        rows.Controls.Add(percent, 2, rowIndex);
        rows.Controls.Add(reset, 3, rowIndex);
    }

    private Label CreateMessageLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = secondaryForeColor,
        BackColor = windowBackColor,
        Margin = new Padding(0, Dip(8), 0, 0),
        AccessibleName = text
    };

    private void UpdateFooter()
    {
        if (footerLabel.IsDisposed)
        {
            return;
        }

        var activeSnapshots = snapshots.Where(snapshot => snapshot.Status != ProviderStatus.Disabled).ToArray();
        if (activeSnapshots.Length == 0)
        {
            footerLabel.Text = "모니터링 비활성화";
            return;
        }

        var latest = activeSnapshots.Max(snapshot => snapshot.RetrievedAt);
        footerLabel.Text = FormatRelativeRefresh(latest, DateTimeOffset.Now);
    }

    private static string FormatRelativeRefresh(DateTimeOffset retrievedAt, DateTimeOffset now)
    {
        var elapsed = now - retrievedAt;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "방금 갱신";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}분 전 갱신";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)}시간 전 갱신";
        }

        return $"{Math.Max(1, (int)elapsed.TotalDays)}일 전 갱신";
    }

    private static string GetProviderName(UsageSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.DisplayName))
        {
            return snapshot.DisplayName;
        }

        return snapshot.ProviderKind switch
        {
            "codex" => "ChatGPT / Codex",
            "claude" => "Claude",
            _ => snapshot.ProviderId
        };
    }

    private Font CreateIconFont()
    {
        var size = baseFontSize * 10f / 9f;
        var font = new Font("Segoe Fluent Icons", size);
        if (string.Equals(font.Name, "Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
        {
            return font;
        }

        // Windows 10 ships only the MDL2 icon font.
        font.Dispose();
        return new Font("Segoe MDL2 Assets", size);
    }

    private Button CreateIconButton(string glyph, string description)
    {
        var button = new IconButton
        {
            Text = glyph,
            Font = iconFont,
            Dock = DockStyle.Right,
            Width = Dip(28),
            FlatStyle = FlatStyle.Flat,
            ForeColor = secondaryForeColor,
            BackColor = windowBackColor,
            Margin = new Padding(0),
            TabStop = true,
            UseVisualStyleBackColor = false,
            AccessibleName = description
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = dividerColor;
        button.FlatAppearance.MouseDownBackColor = dividerColor;
        resetToolTip.SetToolTip(button, description);
        return button;
    }

    private async void RefreshButton_Click(object? sender, EventArgs e)
    {
        if (refreshInProgress)
        {
            return;
        }

        refreshInProgress = true;
        refreshButton.Enabled = false;
        try
        {
            await refreshRequested();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"새로고침에 실패했습니다.\n{exception.Message}",
                "TokenChecker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            refreshInProgress = false;
            if (!IsDisposed)
            {
                refreshButton.Enabled = true;
            }
        }
    }

    private void PinButton_Click(object? sender, EventArgs e)
    {
        ApplyPinned(!pinned);
        pinnedChanged?.Invoke(pinned);
    }

    private void ApplyPinned(bool value)
    {
        pinned = value;
        TopMost = pinned;
        var description = pinned ? "고정 해제" : "고정";
        pinButton.Text = pinned ? UnpinGlyph : PinGlyph;
        pinButton.ForeColor = pinned ? accentColor : secondaryForeColor;
        pinButton.AccessibleName = description;
        resetToolTip.SetToolTip(pinButton, description);
    }

    private bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            var value = key?.GetValue("AppsUseLightTheme", 1);
            return value is not int themeValue || themeValue != 0;
        }
        catch
        {
            return true;
        }
    }

    // A focused flat button becomes the default button and draws a border; the icons should stay bare.
    // Focus cues are left alone so keyboard users still see which icon is selected.
    private sealed class IconButton : Button
    {
        public override void NotifyDefault(bool value) => base.NotifyDefault(false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private int Dip(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private int ToDip(int pixels) => (int)Math.Round(pixels * 96.0 / DeviceDpi);
}
