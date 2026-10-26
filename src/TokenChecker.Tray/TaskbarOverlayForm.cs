using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TokenChecker.Core;

namespace TokenChecker.Tray;

// Windows 11 has no API for third-party content inside the taskbar, so this borderless window floats above
// one monitor's taskbar, just left of the notification area or clock, and follows it while the app runs.
internal sealed class TaskbarOverlayForm : Form
{
    private const int GapDip = 6;
    private const int SidePaddingDip = 6;
    private const int ColumnGapDip = 14;
    private const int MaxNameWidthDip = 96;
    // Below this taskbar height two text lines no longer fit, so each account goes on one line.
    private const int TwoLineMinHeightDip = 36;
    private const int TrackIntervalMilliseconds = 250;
    // Width of the date/time a secondary taskbar draws at its right end, measured at 150% on Windows 11.
    private const int SecondaryClockDip = 110;

    private readonly System.Windows.Forms.Timer trackTimer;
    private readonly ToolTip toolTip;
    // Kept in fields: the native hooks call them for as long as the hooks exist.
    private readonly WinEventProc foregroundChanged;
    private readonly WinEventProc zOrderChanged;
    private IntPtr foregroundHook;
    private IntPtr zOrderHook;
    private uint zOrderHookProcessId;
    private IntPtr trackedTaskbar;
    private bool isPrimaryTaskbar;
    private IReadOnlyList<OverlayEntry> entries = Array.Empty<OverlayEntry>();
    private int warnThresholdPercent = 75;
    private int criticalThresholdPercent = 90;
    private string toolTipText = string.Empty;
    private Rectangle renderedBounds;
    private int renderedDpi;
    private bool contentChanged = true;

    public TaskbarOverlayForm(string screenDeviceName)
    {
        ScreenDeviceName = screenDeviceName ?? throw new ArgumentNullException(nameof(screenDeviceName));
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AccessibleName = "TokenChecker 작업 표시줄 사용량";
        Text = "TokenChecker";

        toolTip = new ToolTip { ShowAlways = true };
        trackTimer = new System.Windows.Forms.Timer { Interval = TrackIntervalMilliseconds };
        trackTimer.Tick += (_, _) => Track();
        foregroundChanged = (_, _, _, _, _, _, _) => Track();
        zOrderChanged = (_, _, _, _, _, _, _) =>
        {
            if (Visible && trackedTaskbar != IntPtr.Zero)
            {
                KeepAbove(trackedTaskbar);
            }
        };
    }

    // Screen.DeviceName of the monitor whose taskbar this overlay sits on.
    public string ScreenDeviceName { get; }

    public event EventHandler? LeftClicked;

    public event EventHandler? RightClicked;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopMost;
            return parameters;
        }
    }

    public void Start()
    {
        if (foregroundHook == IntPtr.Zero)
        {
            // A full-screen window can come to the front at any time; check again at once.
            foregroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                foregroundChanged,
                0,
                0,
                WinEventOutOfContext);
        }

        trackTimer.Start();
        Track();
    }

    public void UpdateSnapshots(
        IReadOnlyList<UsageSnapshot> snapshots,
        UsageDisplayMode displayMode,
        int warnThreshold,
        int criticalThreshold)
    {
        entries = UsageDisplayFormatter.GetOverlayEntries(snapshots ?? Array.Empty<UsageSnapshot>(), displayMode);
        warnThresholdPercent = warnThreshold;
        criticalThresholdPercent = criticalThreshold;
        var text = UsageDisplayFormatter.FormatTooltip(snapshots ?? Array.Empty<UsageSnapshot>(), displayMode);
        if (!string.Equals(text, toolTipText, StringComparison.Ordinal))
        {
            toolTipText = text;
            toolTip.SetToolTip(this, text);
        }

        contentChanged = true;
        Track();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            // Clicking must not take focus away from the popup or whatever the user is working in.
            m.Result = MaNoActivate;
            return;
        }

        base.WndProc(ref m);
        if (m.Msg is WmSettingChange or WmDisplayChange or WmDpiChanged)
        {
            // Theme, scale or taskbar changes: redraw with fresh colors and metrics on the next pass.
            contentChanged = true;
            if (IsHandleCreated)
            {
                BeginInvoke(Track);
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            LeftClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Button == MouseButtons.Right)
        {
            RightClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(foregroundHook);
            foregroundHook = IntPtr.Zero;
        }

        UnhookZOrder();
        if (disposing)
        {
            trackTimer.Dispose();
            toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    // Finds the taskbar again on every pass, so an Explorer restart or a moved taskbar is picked up.
    private void Track()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        var screen = Screen.AllScreens.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, ScreenDeviceName, StringComparison.OrdinalIgnoreCase));
        var taskbar = screen is null ? IntPtr.Zero : FindTaskbar(screen, out isPrimaryTaskbar);
        trackedTaskbar = taskbar;
        // No taskbar there: the monitor is gone, or Windows shows the taskbar on the main display only.
        if (screen is null || taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) ||
            !GetWindowRect(taskbar, out var taskbarRect))
        {
            HideOverlay();
            return;
        }

        HookZOrder(taskbar);

        var taskbarBounds = taskbarRect.ToRectangle();
        var screenBounds = screen.Bounds;
        var onScreen = Rectangle.Intersect(taskbarBounds, screenBounds);
        // A vertical taskbar has no room for the text; an auto-hidden one has slid almost off the screen.
        if (taskbarBounds.Width <= taskbarBounds.Height ||
            onScreen.Height < taskbarBounds.Height - 2 ||
            IsFullScreenWindowActive(taskbar, screenBounds))
        {
            HideOverlay();
            return;
        }

        var dpi = (int)GetDpiForWindow(taskbar);
        if (dpi <= 0)
        {
            dpi = 96;
        }

        // Only the main taskbar has a notification-area window; a secondary one draws just the clock at its
        // right end, with no window to measure, so that space is kept free instead.
        var notifyArea = isPrimaryTaskbar ? FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null) : IntPtr.Zero;
        var right = notifyArea != IntPtr.Zero && GetWindowRect(notifyArea, out var notifyRect) && notifyRect.Left > taskbarBounds.Left
            ? notifyRect.Left
            : taskbarBounds.Right - Scale(isPrimaryTaskbar ? 200 : SecondaryClockDip, dpi);
        right -= Scale(GapDip, dpi);

        if (contentChanged || renderedBounds.Height != taskbarBounds.Height || renderedBounds.Right != right ||
            renderedBounds.Top != taskbarBounds.Top || renderedDpi != dpi)
        {
            Render(right, taskbarBounds.Top, taskbarBounds.Height, Math.Max(0, right - taskbarBounds.Left), dpi);
        }

        if (!Visible)
        {
            Show();
        }

        KeepAbove(taskbar);
    }

    // The main taskbar is Shell_TrayWnd; with "show taskbar on all displays" every other monitor has a
    // Shell_SecondaryTrayWnd. Each is matched to a monitor by the screen it is on.
    private static IntPtr FindTaskbar(Screen screen, out bool isPrimary)
    {
        foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var candidate = IntPtr.Zero;
            while ((candidate = FindWindowEx(IntPtr.Zero, candidate, className, null)) != IntPtr.Zero)
            {
                if (string.Equals(Screen.FromHandle(candidate).DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    isPrimary = className == "Shell_TrayWnd";
                    return candidate;
                }
            }
        }

        isPrimary = false;
        return IntPtr.Zero;
    }

    private void HideOverlay()
    {
        if (Visible)
        {
            Hide();
        }
    }

    // Draws the content to a per-pixel-alpha bitmap so the taskbar's own background shows through.
    private void Render(int right, int top, int height, int maxWidth, int dpi)
    {
        var lightTaskbar = IsLightTaskbar();
        var primary = lightTaskbar ? Color.FromArgb(20, 20, 20) : Color.FromArgb(255, 255, 255);
        var secondary = lightTaskbar ? Color.FromArgb(90, 90, 90) : Color.FromArgb(200, 200, 200);
        var warn = lightTaskbar ? Color.FromArgb(176, 98, 0) : Color.FromArgb(255, 190, 80);
        var critical = lightTaskbar ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 120, 110);

        var twoLines = height >= Scale(TwoLineMinHeightDip, dpi);
        var fontPixels = twoLines ? Math.Min(Scale(12, dpi), (height - Scale(8, dpi)) / 2.6f) : Math.Min(Scale(12, dpi), height / 1.8f);
        using var font = new Font("Segoe UI", Math.Max(6f, fontPixels), FontStyle.Regular, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", Math.Max(6f, fontPixels), FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;

        var lineHeight = (int)Math.Ceiling(font.GetHeight());
        var padding = Scale(SidePaddingDip, dpi);
        var columnGap = Scale(ColumnGapDip, dpi);
        var maxNameWidth = Scale(MaxNameWidthDip, dpi);

        var columns = new List<Column>();
        using (var measureBitmap = new Bitmap(1, 1))
        using (var measure = Graphics.FromImage(measureBitmap))
        {
            measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var shown = entries.Count == 0
                ? new[] { new OverlayEntry("TokenChecker", false, Array.Empty<OverlayValue>(), "모니터링 비활성화") }
                : entries;
            foreach (var entry in shown)
            {
                // Only the account name is shortened; the plan stays readable after it.
                var nameLine = new List<Segment>
                {
                    new(Ellipsize(measure, entry.IsStale ? entry.Name + "*" : entry.Name, nameFont, format, maxNameWidth), secondary, nameFont)
                };
                if (entry.Plan is not null)
                {
                    nameLine.Add(new Segment(" " + entry.Plan, secondary, font));
                }

                var segments = new List<Segment>();
                if (entry.StatusText is not null)
                {
                    segments.Add(new Segment(entry.StatusText, warn, font));
                }
                else
                {
                    for (var index = 0; index < entry.Values.Count; index++)
                    {
                        var value = entry.Values[index];
                        var prefix = index == 0 ? value.Label + " " : "  " + value.Label + " ";
                        segments.Add(new Segment(prefix, secondary, font));
                        segments.Add(new Segment(value.Text, GetValueColor(value.UsedPercent, primary, warn, critical), font));
                    }
                }

                if (!twoLines)
                {
                    nameLine.Add(new Segment("  ", secondary, font));
                    segments.InsertRange(0, nameLine);
                }

                var nameWidth = twoLines ? MeasureLine(measure, nameLine, format) : 0;
                var valuesWidth = MeasureLine(measure, segments, format);
                columns.Add(new Column(twoLines ? nameLine : null, segments, Math.Max(nameWidth, valuesWidth)));
            }
        }

        var width = (padding * 2) + columns.Sum(column => column.Width) + (columnGap * Math.Max(0, columns.Count - 1));
        // Never grow left past the taskbar's start; the text is clipped instead.
        width = Math.Max(1, Math.Min(width, Math.Max(1, maxWidth)));
        var bounds = new Rectangle(right - width, top, width, height);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Nearly invisible, but non-zero alpha keeps the whole strip clickable.
            graphics.Clear(Color.FromArgb(1, 0, 0, 0));

            var x = (float)padding;
            var linesHeight = twoLines ? lineHeight * 2 : lineHeight;
            var y = (height - linesHeight) / 2f;
            foreach (var column in columns)
            {
                var lineY = y;
                if (column.NameLine is not null)
                {
                    DrawLine(graphics, column.NameLine, x, lineY, format);
                    lineY += lineHeight;
                }

                DrawLine(graphics, column.Segments, x, lineY, format);
                x += column.Width + columnGap;
            }
        }

        ApplyBitmap(bitmap, bounds);
        renderedBounds = bounds;
        renderedDpi = dpi;
        contentChanged = false;
    }

    private Color GetValueColor(double usedPercent, Color normal, Color warn, Color critical)
    {
        if (!double.IsFinite(usedPercent))
        {
            return normal;
        }

        var warnAt = Math.Clamp(warnThresholdPercent, 0, 100);
        var criticalAt = Math.Clamp(criticalThresholdPercent, warnAt, 100);
        return usedPercent >= criticalAt ? critical : usedPercent >= warnAt ? warn : normal;
    }

    private static void DrawText(Graphics graphics, string text, Font font, Color color, float x, float y, StringFormat format)
    {
        using var brush = new SolidBrush(color);
        graphics.DrawString(text, font, brush, x, y, format);
    }

    private static void DrawLine(Graphics graphics, IReadOnlyList<Segment> segments, float x, float y, StringFormat format)
    {
        foreach (var segment in segments)
        {
            DrawText(graphics, segment.Text, segment.Font, segment.Color, x, y, format);
            x += Measure(graphics, segment.Text, segment.Font, format);
        }
    }

    private static int MeasureLine(Graphics graphics, IReadOnlyList<Segment> segments, StringFormat format) =>
        segments.Sum(segment => Measure(graphics, segment.Text, segment.Font, format));

    private static int Measure(Graphics graphics, string text, Font font, StringFormat format) =>
        text.Length == 0 ? 0 : (int)Math.Ceiling(graphics.MeasureString(text, font, int.MaxValue, format).Width);

    private static string Ellipsize(Graphics graphics, string text, Font font, StringFormat format, int maxWidth)
    {
        if (Measure(graphics, text, font, format) <= maxWidth)
        {
            return text;
        }

        var builder = new StringBuilder(text);
        while (builder.Length > 1)
        {
            builder.Length--;
            var candidate = builder.ToString().TrimEnd() + "…";
            if (Measure(graphics, candidate, font, format) <= maxWidth)
            {
                return candidate;
            }
        }

        return "…";
    }

    private void ApplyBitmap(Bitmap bitmap, Rectangle bounds)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memoryDc, hBitmap);
            var size = new NativeSize(bounds.Width, bounds.Height);
            var source = new NativePoint(0, 0);
            var destination = new NativePoint(bounds.Left, bounds.Top);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
                DeleteObject(hBitmap);
            }

            DeleteDC(memoryDc);
        }
    }

    // A click on the taskbar lifts it above this window without always changing the foreground window, and
    // waiting for the timer left the overlay hidden for ~200 ms (a visible blink). Explorer reports every
    // z-order change of its windows, so the overlay is put back within the same frame instead.
    private void HookZOrder(IntPtr taskbar)
    {
        GetWindowThreadProcessId(taskbar, out var processId);
        if (processId == 0 || (zOrderHook != IntPtr.Zero && processId == zOrderHookProcessId))
        {
            return;
        }

        // A new Explorer process (after a restart) needs a new hook.
        UnhookZOrder();
        zOrderHook = SetWinEventHook(
            EventObjectReorder,
            EventObjectReorder,
            IntPtr.Zero,
            zOrderChanged,
            processId,
            0,
            WinEventOutOfContext);
        zOrderHookProcessId = zOrderHook == IntPtr.Zero ? 0 : processId;
    }

    private void UnhookZOrder()
    {
        if (zOrderHook != IntPtr.Zero)
        {
            UnhookWinEvent(zOrderHook);
            zOrderHook = IntPtr.Zero;
            zOrderHookProcessId = 0;
        }
    }

    // Puts the overlay back only when the taskbar is above it, so it does not keep jumping over unrelated
    // topmost windows such as menus.
    private void KeepAbove(IntPtr taskbar)
    {
        var current = GetWindow(Handle, GwHwndPrev);
        for (var guard = 0; current != IntPtr.Zero && guard < 512; guard++)
        {
            if (current == taskbar)
            {
                SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
                return;
            }

            current = GetWindow(current, GwHwndPrev);
        }
    }

    // A video or game covering the whole taskbar monitor would otherwise have the overlay drawn on top of it.
    private bool IsFullScreenWindowActive(IntPtr taskbar, Rectangle screenBounds)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == taskbar || foreground == Handle)
        {
            return false;
        }

        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW")
        {
            return false;
        }

        return GetWindowRect(foreground, out var rect) && rect.ToRectangle().Contains(screenBounds);
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            // The taskbar follows the Windows (system) mode, not the app mode.
            return key?.GetValue("SystemUsesLightTheme", 0) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static int Scale(int value, int dpi) => (int)Math.Round(value * dpi / 96.0);

    private sealed record Segment(string Text, Color Color, Font Font);

    // NameLine is null in one-line mode, where the name leads Segments instead.
    private sealed record Column(IReadOnlyList<Segment>? NameLine, IReadOnlyList<Segment> Segments, int Width);

    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopMost = 0x00000008;
    private const int WmMouseActivate = 0x0021;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private static readonly IntPtr MaNoActivate = new(3);
    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint GwHwndPrev = 3;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectReorder = 0x8004;
    private const uint WinEventOutOfContext = 0x0000;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd,
        IntPtr destinationDc,
        ref NativePoint destination,
        ref NativeSize size,
        IntPtr sourceDc,
        ref NativePoint source,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);
}
