using System.Text.Json;

namespace TokenChecker.Core;

public static class CodexUsageMapper
{
    public static string? ParsePlanType(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) &&
            byLimitId.ValueKind == JsonValueKind.Object &&
            byLimitId.TryGetProperty("codex", out var codex))
        {
            return GetPlanType(codex);
        }

        if (!result.TryGetProperty("rateLimits", out var rateLimits) ||
            rateLimits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (rateLimits.TryGetProperty("limitId", out var limitId) &&
            (limitId.ValueKind != JsonValueKind.String ||
             !string.Equals(limitId.GetString(), "codex", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return GetPlanType(rateLimits);
    }

    public static IReadOnlyList<UsageWindow> Parse(JsonElement result)
    {
        var windows = new List<UsageWindow>();
        if (result.ValueKind != JsonValueKind.Object)
        {
            return windows;
        }

        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) && byLimitId.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in byLimitId.EnumerateObject())
            {
                AddBucket(windows, bucket.Name, bucket.Value);
            }

            return windows;
        }

        if (result.TryGetProperty("rateLimits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
        {
            var limitId = rateLimits.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? "codex"
                : "codex";
            AddBucket(windows, limitId, rateLimits);
        }

        return windows;
    }

    private static void AddBucket(List<UsageWindow> windows, string limitId, JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        AddWindow(windows, limitId, "primary", bucket);
        AddWindow(windows, limitId, "secondary", bucket);
    }

    private static void AddWindow(List<UsageWindow> windows, string limitId, string slot, JsonElement bucket)
    {
        if (!bucket.TryGetProperty(slot, out var element) || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number ||
            !used.TryGetDouble(out var usedPercent) || !double.IsFinite(usedPercent))
        {
            return;
        }

        var duration = TryGetDuration(element, out var parsedDuration) ? parsedDuration : TimeSpan.Zero;
        var resetsAt = TryGetReset(element, out var parsedReset) ? parsedReset : null;
        var scope = string.Equals(limitId, "codex", StringComparison.OrdinalIgnoreCase)
            ? UsageScope.Overall
            : UsageScope.Unknown;

        windows.Add(new UsageWindow(
            $"{limitId}:{slot}",
            UsageWindowClassifier.GetDisplayName(duration),
            scope,
            usedPercent,
            duration,
            resetsAt));
    }

    private static string? GetPlanType(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object ||
            !bucket.TryGetProperty("planType", out var planType) ||
            planType.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = planType.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryGetDuration(JsonElement element, out TimeSpan duration)
    {
        duration = default;
        if (!element.TryGetProperty("windowDurationMins", out var minutes) ||
            minutes.ValueKind != JsonValueKind.Number ||
            !minutes.TryGetDouble(out var value) ||
            !double.IsFinite(value) ||
            value < TimeSpan.MinValue.TotalMinutes ||
            value > TimeSpan.MaxValue.TotalMinutes)
        {
            return false;
        }

        duration = TimeSpan.FromMinutes(value);
        return true;
    }

    private static bool TryGetReset(JsonElement element, out DateTimeOffset? resetsAt)
    {
        resetsAt = null;
        if (!element.TryGetProperty("resetsAt", out var reset) ||
            reset.ValueKind != JsonValueKind.Number ||
            !reset.TryGetInt64(out var unixSeconds))
        {
            return false;
        }

        try
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
