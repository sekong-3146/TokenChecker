using System.Globalization;

namespace TokenChecker.Core;

public static class UsageDisplayFormatter
{
    private const int MaxTooltipLength = 127;

    public static UsageWindow? SelectWindow(UsageSnapshot snapshot, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var matching = snapshot.Windows
            .Where(window => window.WindowDuration == duration)
            .ToArray();
        if (IsCodexPro(snapshot))
        {
            return duration == TimeSpan.FromDays(7)
                ? matching.FirstOrDefault(window => window.Scope == UsageScope.Overall)
                : null;
        }

        return matching.FirstOrDefault(window => window.Scope == UsageScope.Overall)
            ?? matching.FirstOrDefault();
    }

    public static bool IsCodexPro(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var providerKind = snapshot.ProviderKind ?? snapshot.ProviderId;
        var isCodex = string.Equals(providerKind, "codex", StringComparison.OrdinalIgnoreCase);
        return isCodex && IsCodexProPlan(snapshot.PlanType);
    }

    // "prolite" was observed live on 2026-10-07 returning only the weekly window, like "pro".
    public static bool IsCodexProPlan(string? planType) => planType?.Trim().ToLowerInvariant() is "pro" or "prolite";

    public static string FormatPlan(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var plan = snapshot.PlanType?.Trim().ToLowerInvariant();
        // Claude Max multiplier, e.g. "max_5x" from ClaudeUsageProvider.
        if (plan is { Length: > 5 and <= 8 } && plan.StartsWith("max_", StringComparison.Ordinal) &&
            plan.EndsWith('x') && plan[4..^1].All(char.IsAsciiDigit))
        {
            return $"Max {plan[4..^1]}x";
        }

        return plan switch
        {
            "free" => "Free",
            "plus" => "Plus",
            "pro" => "Pro",
            // "pro" may cover both the $200 and $500 tiers, so only "prolite" carries a price.
            "prolite" => "Pro 100",
            "team" => "Team",
            "max" => "Max",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "edu" => "Edu",
            _ => "확인 불가"
        };
    }

    public static string FormatPercent(double usedPercent, UsageDisplayMode mode)
    {
        if (!double.IsFinite(usedPercent))
        {
            return "--";
        }

        var percent = mode == UsageDisplayMode.Remaining ? 100d - usedPercent : usedPercent;
        return $"{percent.ToString("0", CultureInfo.InvariantCulture)}%";
    }

    public static string FormatTooltip(IReadOnlyList<UsageSnapshot> snapshots, UsageDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var lines = snapshots
            .Where(snapshot => snapshot.Status != ProviderStatus.Disabled)
            .Select(snapshot =>
            {
                var name = !string.IsNullOrWhiteSpace(snapshot.DisplayName)
                    ? snapshot.DisplayName!
                    : GetProviderName(snapshot.ProviderId);
                var staleSuffix = snapshot.Status == ProviderStatus.Stale ? "*" : string.Empty;
                var weekly = SelectWindow(snapshot, TimeSpan.FromDays(7));
                var weeklyText = weekly is null ? "--" : FormatPercent(weekly.UsedPercent, mode);
                if (IsCodexPro(snapshot))
                {
                    return $"{name}{staleSuffix} W {weeklyText}";
                }

                var fiveHour = SelectWindow(snapshot, TimeSpan.FromHours(5));
                var fiveHourText = fiveHour is null ? "--" : FormatPercent(fiveHour.UsedPercent, mode);
                return $"{name}{staleSuffix} 5h {fiveHourText} | W {weeklyText}";
            })
            .ToArray();
        var tooltip = lines.Length == 0
            ? "모니터링 비활성화"
            : string.Join(Environment.NewLine, lines);
        if (tooltip.Length <= MaxTooltipLength)
        {
            return tooltip;
        }

        var truncated = tooltip[..MaxTooltipLength];
        var lastBreak = truncated.LastIndexOf(Environment.NewLine, StringComparison.Ordinal);
        return lastBreak > 0 ? truncated[..lastBreak] : truncated;
    }

    public static string FormatReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (!resetsAt.HasValue)
        {
            return "--";
        }

        var local = resetsAt.Value.ToLocalTime();
        return resetsAt.Value - now <= TimeSpan.FromHours(24)
            ? $"{local:HH:mm} 초기화"
            : $"{GetWeekday(local.DayOfWeek)} {local:HH:mm}";
    }

    public static string FormatResetAccessible(DateTimeOffset? resetsAt)
    {
        return resetsAt.HasValue
            ? resetsAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "--";
    }

    public static string FormatStatus(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var providerKind = (snapshot.ProviderKind ?? snapshot.ProviderId).ToLowerInvariant();
        return snapshot.Status switch
        {
            ProviderStatus.Disabled => "모니터링 비활성화",
            ProviderStatus.Stale => "오래됨",
            ProviderStatus.Ready => "정상",
            ProviderStatus.NotInstalled when providerKind == "codex" => "Codex CLI 없음",
            ProviderStatus.NotInstalled when providerKind == "claude" => "Claude Code 없음",
            ProviderStatus.NotInstalled => "설치되지 않음",
            ProviderStatus.SignedOut when providerKind == "codex" => "Codex 로그인 필요",
            ProviderStatus.SignedOut when providerKind == "claude" => "Claude 로그인 필요",
            ProviderStatus.SignedOut => "로그인 필요",
            ProviderStatus.NetworkError => "네트워크 오류",
            ProviderStatus.InvalidResponse => "응답 오류",
            ProviderStatus.RateLimited => "요청 제한",
            ProviderStatus.Forbidden => "권한 오류",
            ProviderStatus.UnknownError => "알 수 없는 오류",
            _ => "알 수 없는 오류"
        };
    }

    public static double MaxUsedPercent(IReadOnlyList<UsageSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var maximum = double.MinValue;
        var found = false;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Status is not (ProviderStatus.Ready or ProviderStatus.Stale))
            {
                continue;
            }

            var windows = IsCodexPro(snapshot)
                ? new[] { SelectWindow(snapshot, TimeSpan.FromDays(7)) }.OfType<UsageWindow>()
                : snapshot.Windows;
            foreach (var window in windows)
            {
                if (double.IsFinite(window.UsedPercent) && (!found || window.UsedPercent > maximum))
                {
                    maximum = window.UsedPercent;
                    found = true;
                }
            }
        }

        return found ? maximum : -1;
    }

    private static string GetProviderName(string providerId) => providerId.ToLowerInvariant() switch
    {
        "codex" => "GPT",
        "claude" => "Claude",
        _ => providerId
    };

    private static string GetWeekday(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => "월",
        DayOfWeek.Tuesday => "화",
        DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목",
        DayOfWeek.Friday => "금",
        DayOfWeek.Saturday => "토",
        _ => "일"
    };
}
