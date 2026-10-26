namespace TokenChecker.Core;

public sealed record DisplayScreen(string DeviceName, bool Primary);

public static class TaskbarOverlayTargets
{
    // Device names of the monitors whose taskbar gets the overlay, primary first.
    public static IReadOnlyList<string> Resolve(string? setting, IReadOnlyList<DisplayScreen> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);

        var primary = screens.FirstOrDefault(screen => screen.Primary) ?? screens.FirstOrDefault();
        if (primary is null)
        {
            return Array.Empty<string>();
        }

        if (string.Equals(setting?.Trim(), AppSettings.AllTaskbarOverlayScreens, StringComparison.Ordinal))
        {
            return screens
                .OrderByDescending(screen => screen.Primary)
                .Select(screen => screen.DeviceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // A chosen monitor that is not connected right now falls back to the primary one.
        var chosen = string.IsNullOrWhiteSpace(setting)
            ? null
            : screens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, setting.Trim(), StringComparison.OrdinalIgnoreCase));
        return new[] { (chosen ?? primary).DeviceName };
    }
}
