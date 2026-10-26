namespace TokenChecker.Core;

public enum UsageScope
{
    Overall,
    Model,
    Unknown
}

public sealed record UsageWindow(
    string Id,
    string DisplayName,
    UsageScope Scope,
    double UsedPercent,
    TimeSpan WindowDuration,
    DateTimeOffset? ResetsAt);

// UsedPercent is always the used share (NaN when unknown), so colors follow the thresholds in either display mode.
public sealed record OverlayValue(string Label, string Text, double UsedPercent);

// StatusText replaces the values when the account has no usable data; Plan is null when it is unknown.
public sealed record OverlayEntry(
    string Name,
    bool IsStale,
    IReadOnlyList<OverlayValue> Values,
    string? StatusText,
    string? Plan = null);

public static class UsageWindowClassifier
{
    public static string GetDisplayName(TimeSpan duration) => duration switch
    {
        var value when value == TimeSpan.FromHours(5) => "5 hours",
        var value when value == TimeSpan.FromDays(7) => "Weekly",
        _ => $"{duration.TotalMinutes:0} minutes"
    };
}
