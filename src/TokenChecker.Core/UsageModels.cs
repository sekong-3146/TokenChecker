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

public static class UsageWindowClassifier
{
    public static string GetDisplayName(TimeSpan duration) => duration switch
    {
        var value when value == TimeSpan.FromHours(5) => "5 hours",
        var value when value == TimeSpan.FromDays(7) => "Weekly",
        _ => $"{duration.TotalMinutes:0} minutes"
    };
}
