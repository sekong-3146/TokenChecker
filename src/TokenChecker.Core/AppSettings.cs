namespace TokenChecker.Core;

public enum UsageDisplayMode
{
    Used,
    Remaining
}

public sealed class AppSettings
{
    public const int MaxAgents = 4;

    // Capped so the popup can never be made invisible.
    public const int MaxPopupTransparencyPercent = 80;

    public int RefreshIntervalMinutes { get; set; } = 5;

    public UsageDisplayMode DisplayMode { get; set; } = UsageDisplayMode.Used;

    public int WarnThresholdPercent { get; set; } = 75;

    public int CriticalThresholdPercent { get; set; } = 90;

    public bool StartWithWindows { get; set; }

    public string? CodexExecutablePath { get; set; }

    public bool CodexEnabled { get; set; } = true;

    public bool ClaudeEnabled { get; set; } = true;

    public List<AgentAccount>? Agents { get; set; }

    // Popup size in 96-DPI units; null means the default size.
    public int? PopupWidth { get; set; }

    public int? PopupHeight { get; set; }

    public bool PopupPinned { get; set; }

    // Windows display device name (e.g. \\.\DISPLAY2); null shows the popup on the clicked screen.
    public string? PopupScreen { get; set; }

    // 0 is opaque; see MaxPopupTransparencyPercent.
    public int PopupTransparencyPercent { get; set; }

    // Shows a compact usage strip over the primary taskbar, next to the notification area.
    public bool TaskbarOverlayEnabled { get; set; }

    public IReadOnlyList<AgentAccount> GetAgents()
    {
        if (Agents is not null)
        {
            return Agents;
        }

        return new[]
        {
            new AgentAccount
            {
                Id = "codex",
                Kind = AgentKind.Codex,
                Name = "Codex",
                Enabled = CodexEnabled,
                UseDefaultCredentials = true
            },
            new AgentAccount
            {
                Id = "claude",
                Kind = AgentKind.Claude,
                Name = "Claude",
                Enabled = ClaudeEnabled,
                UseDefaultCredentials = true
            }
        };
    }

    public AppSettings Clone() => new()
    {
        RefreshIntervalMinutes = RefreshIntervalMinutes,
        DisplayMode = DisplayMode,
        WarnThresholdPercent = WarnThresholdPercent,
        CriticalThresholdPercent = CriticalThresholdPercent,
        StartWithWindows = StartWithWindows,
        CodexExecutablePath = CodexExecutablePath,
        CodexEnabled = CodexEnabled,
        ClaudeEnabled = ClaudeEnabled,
        PopupWidth = PopupWidth,
        PopupHeight = PopupHeight,
        PopupPinned = PopupPinned,
        PopupScreen = PopupScreen,
        PopupTransparencyPercent = PopupTransparencyPercent,
        TaskbarOverlayEnabled = TaskbarOverlayEnabled,
        Agents = Agents?
            .Where(account => account is not null)
            .Select(account => account!.Clone())
            .ToList()
    };
}
