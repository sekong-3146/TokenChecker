using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TokenChecker.Tray;

internal static class TrayIconRenderer
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<IconKey, Icon> Cache = new();

    public static Icon GetIcon(double maxUsedPercent, int warnThresholdPercent, int criticalThresholdPercent, int dpi = 96)
    {
        var size = GetIconSize(dpi);
        var color = GetGaugeColor(maxUsedPercent, warnThresholdPercent, criticalThresholdPercent);
        var key = new IconKey(size, color.ToArgb());

        lock (SyncRoot)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var icon = CreateIcon(size, color);
            Cache.Add(key, icon);
            return icon;
        }
    }

    public static int GetCurrentDpi()
    {
        try
        {
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round(graphics.DpiX);
        }
        catch
        {
            return 96;
        }
    }

    public static void Dispose()
    {
        lock (SyncRoot)
        {
            foreach (var icon in Cache.Values)
            {
                icon.Dispose();
            }

            Cache.Clear();
        }
    }

    private static Icon CreateIcon(int size, Color color)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.Clear(Color.Transparent);

            var stroke = Math.Max(1.5f, size / 7f);
            var bounds = new RectangleF(stroke, stroke, size - (stroke * 2), size - (stroke * 2));
            using var trackPen = new Pen(Color.FromArgb(75, color), stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var gaugePen = new Pen(color, stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            graphics.DrawArc(gaugePen, bounds, 135, 270);

            var center = new PointF(size / 2f, size / 2f);
            var radians = 225f * MathF.PI / 180f;
            var needleLength = size * 0.28f;
            var needleEnd = new PointF(
                center.X + (MathF.Cos(radians) * needleLength),
                center.Y + (MathF.Sin(radians) * needleLength));
            using var needlePen = new Pen(color, Math.Max(1f, size / 10f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(needlePen, center, needleEnd);
            using var centerBrush = new SolidBrush(color);
            graphics.FillEllipse(centerBrush, center.X - stroke, center.Y - stroke, stroke * 2, stroke * 2);
        }

        var handle = IntPtr.Zero;
        try
        {
            handle = bitmap.GetHicon();
            using var nativeIcon = Icon.FromHandle(handle);
            return (Icon)nativeIcon.Clone();
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                DestroyIcon(handle);
            }
        }
    }

    private static int GetIconSize(int dpi) => dpi switch
    {
        >= 192 => 32,
        >= 144 => 24,
        >= 120 => 20,
        _ => 16
    };

    private static Color GetGaugeColor(double maxUsedPercent, int warnThresholdPercent, int criticalThresholdPercent)
    {
        if (maxUsedPercent < 0)
        {
            return Color.FromArgb(128, 128, 128);
        }

        var warn = Math.Clamp(warnThresholdPercent, 0, 100);
        var critical = Math.Clamp(criticalThresholdPercent, warn, 100);
        if (maxUsedPercent >= critical)
        {
            return Color.FromArgb(210, 55, 50);
        }

        if (maxUsedPercent >= warn)
        {
            return Color.FromArgb(224, 142, 28);
        }

        return Color.FromArgb(66, 112, 165);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly record struct IconKey(int Size, int ColorArgb);
}
