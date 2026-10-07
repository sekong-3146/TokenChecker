using System.Text.Json;

namespace TokenChecker.Core;

public static class ClaudeUsageMapper
{
    public static IReadOnlyList<UsageWindow> Parse(JsonElement root)
    {
        var windows = new List<UsageWindow>();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("utilization", out var utilization) ||
                utilization.ValueKind != JsonValueKind.Number ||
                !utilization.TryGetDouble(out var usedPercent))
            {
                continue;
            }

            var duration = GetDuration(property.Name);
            windows.Add(new UsageWindow(
                property.Name,
                GetDisplayName(property.Name, duration),
                GetScope(property.Name),
                usedPercent,
                duration,
                GetResetAt(property.Value)));
        }

        return windows;
    }

    private static TimeSpan GetDuration(string id) => id switch
    {
        "five_hour" => TimeSpan.FromHours(5),
        "seven_day" => TimeSpan.FromDays(7),
        _ => TimeSpan.Zero
    };

    private static UsageScope GetScope(string id) => id switch
    {
        "five_hour" or "seven_day" => UsageScope.Overall,
        _ => UsageScope.Unknown
    };

    private static string GetDisplayName(string id, TimeSpan duration) => duration == TimeSpan.Zero
        ? id
        : UsageWindowClassifier.GetDisplayName(duration);

    private static DateTimeOffset? GetResetAt(JsonElement element)
    {
        if (!element.TryGetProperty("resets_at", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }
}
