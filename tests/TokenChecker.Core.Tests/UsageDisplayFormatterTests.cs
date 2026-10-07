using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class UsageDisplayFormatterTests
{
    [TestMethod]
    public void FormatPercent_FormatsUsedAndRemainingValues()
    {
        Assert.AreEqual("34%", UsageDisplayFormatter.FormatPercent(34, UsageDisplayMode.Used));
        Assert.AreEqual("66%", UsageDisplayFormatter.FormatPercent(34, UsageDisplayMode.Remaining));
        Assert.AreEqual("--", UsageDisplayFormatter.FormatPercent(double.NaN, UsageDisplayMode.Used));
    }

    [TestMethod]
    public void FormatPlan_FormatsKnownPlansAndRejectsMissingOrUnknownValues()
    {
        var known = new[]
        {
            (Value: " pro ", Expected: "Pro"),
            (Value: "prolite", Expected: "Pro 100"),
            (Value: "PLUS", Expected: "Plus"),
            (Value: "free", Expected: "Free"),
            (Value: "team", Expected: "Team"),
            (Value: "max", Expected: "Max"),
            (Value: "max_5x", Expected: "Max 5x"),
            (Value: "MAX_20X", Expected: "Max 20x"),
            (Value: "max_x", Expected: "확인 불가"),
            (Value: "business", Expected: "Business"),
            (Value: "enterprise", Expected: "Enterprise"),
            (Value: "edu", Expected: "Edu")
        };

        foreach (var plan in known)
        {
            Assert.AreEqual(plan.Expected, UsageDisplayFormatter.FormatPlan(CreateSnapshot("codex", "codex", plan.Value)));
        }

        Assert.AreEqual("확인 불가", UsageDisplayFormatter.FormatPlan(CreateSnapshot("codex", "codex", null)));
        Assert.AreEqual("확인 불가", UsageDisplayFormatter.FormatPlan(CreateSnapshot("codex", "codex", "future_plan")));
    }

    [TestMethod]
    public void IsCodexPro_UsesExplicitKindBeforeLegacyProviderId()
    {
        Assert.IsTrue(UsageDisplayFormatter.IsCodexPro(CreateSnapshot("codex", null, "PRO")));
        Assert.IsTrue(UsageDisplayFormatter.IsCodexPro(CreateSnapshot("account-1", "codex", "pro")));
        Assert.IsTrue(UsageDisplayFormatter.IsCodexPro(CreateSnapshot("account-1", "codex", "ProLite")));
        Assert.IsFalse(UsageDisplayFormatter.IsCodexPro(CreateSnapshot("codex", "claude", "pro")));
        Assert.IsFalse(UsageDisplayFormatter.IsCodexPro(CreateSnapshot("codex", "codex", "plus")));
    }

    [TestMethod]
    public void FormatTooltip_CodexProShowsOnlyOverallWeeklyRemaining()
    {
        var snapshot = new UsageSnapshot(
            "codex-account",
            ProviderStatus.Ready,
            new[]
            {
                CreateWindow("codex:primary", 1, TimeSpan.FromDays(7), UsageScope.Overall),
                CreateWindow("codex_bengalfox:primary", 99, TimeSpan.FromDays(7), UsageScope.Unknown),
                CreateWindow("codex_bengalfox:secondary", 98, TimeSpan.FromHours(5), UsageScope.Unknown)
            },
            DateTimeOffset.UtcNow,
            DisplayName: "Codex Pro",
            ProviderKind: "codex",
            PlanType: "pro");

        var tooltip = UsageDisplayFormatter.FormatTooltip(new[] { snapshot }, UsageDisplayMode.Remaining);

        Assert.AreEqual("Codex Pro W 99%", tooltip);
        Assert.IsFalse(tooltip.Contains("5h", StringComparison.Ordinal));
        Assert.AreEqual("codex:primary", UsageDisplayFormatter.SelectWindow(snapshot, TimeSpan.FromDays(7))?.Id);
        Assert.IsNull(UsageDisplayFormatter.SelectWindow(snapshot, TimeSpan.FromHours(5)));
        Assert.AreEqual(1d, UsageDisplayFormatter.MaxUsedPercent(new[] { snapshot }));
    }

    [TestMethod]
    public void FormatTooltip_ClaudeProAndPlusKeepFiveHourWindow()
    {
        var snapshots = new[]
        {
            CreateSnapshot("claude-pro", "claude", "pro", CreateWindow("pro-5h", 20, TimeSpan.FromHours(5))),
            CreateSnapshot("claude-plus", "claude", "plus", CreateWindow("plus-5h", 30, TimeSpan.FromHours(5)))
        };

        var tooltip = UsageDisplayFormatter.FormatTooltip(snapshots, UsageDisplayMode.Used);

        Assert.IsTrue(tooltip.Contains("claude-pro 5h 20%", StringComparison.Ordinal));
        Assert.IsTrue(tooltip.Contains("claude-plus 5h 30%", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormatTooltip_FormatsWindowsStaleMarkerMissingWindowsAndDisabledProviders()
    {
        var snapshots = new[]
        {
            new UsageSnapshot(
                "codex",
                ProviderStatus.Ready,
                new[]
                {
                    CreateWindow("codex-5h", 34, TimeSpan.FromHours(5)),
                    CreateWindow("codex-week", 52, TimeSpan.FromDays(7))
                },
                DateTimeOffset.UtcNow),
            new UsageSnapshot(
                "claude",
                ProviderStatus.Stale,
                new[] { CreateWindow("claude-5h", 20, TimeSpan.FromHours(5)) },
                DateTimeOffset.UtcNow),
            new UsageSnapshot("disabled", ProviderStatus.Disabled, Array.Empty<UsageWindow>(), DateTimeOffset.UtcNow)
        };

        var tooltip = UsageDisplayFormatter.FormatTooltip(snapshots, UsageDisplayMode.Used);

        var expected = string.Join(
            Environment.NewLine,
            "GPT 5h 34% | W 52%",
            "Claude* 5h 20% | W --");
        Assert.AreEqual(expected, tooltip);
        Assert.IsFalse(tooltip.Contains("GPT*", StringComparison.Ordinal));
        Assert.IsTrue(tooltip.Contains("Claude*", StringComparison.Ordinal));

        var allDisabled = UsageDisplayFormatter.FormatTooltip(
            new[]
            {
                new UsageSnapshot("codex", ProviderStatus.Disabled, Array.Empty<UsageWindow>(), DateTimeOffset.UtcNow),
                new UsageSnapshot("claude", ProviderStatus.Disabled, Array.Empty<UsageWindow>(), DateTimeOffset.UtcNow)
            },
            UsageDisplayMode.Used);
        Assert.AreEqual("모니터링 비활성화", allDisabled);
    }

    [TestMethod]
    public void FormatTooltip_LimitsPathologicalInputTo127Characters()
    {
        var snapshots = Enumerable.Range(0, 20)
            .Select(index => new UsageSnapshot(
                $"provider-{index}-{new string('x', 80)}",
                ProviderStatus.Ready,
                new[] { CreateWindow($"window-{index}", 50, TimeSpan.FromHours(5)) },
                DateTimeOffset.UtcNow))
            .ToArray();

        var tooltip = UsageDisplayFormatter.FormatTooltip(snapshots, UsageDisplayMode.Used);

        Assert.IsTrue(tooltip.Length <= 127, $"Tooltip length was {tooltip.Length}.");
    }

    [TestMethod]
    public void FormatTooltip_UsesAccountDisplayNameAndProviderKindForStatus()
    {
        var snapshot = new UsageSnapshot(
            "account-1",
            ProviderStatus.SignedOut,
            Array.Empty<UsageWindow>(),
            DateTimeOffset.UtcNow,
            DisplayName: "회사 계정",
            ProviderKind: "claude");

        Assert.IsTrue(UsageDisplayFormatter.FormatTooltip(new[] { snapshot }, UsageDisplayMode.Used)
            .StartsWith("회사 계정 5h", StringComparison.Ordinal));
        Assert.AreEqual("Claude 로그인 필요", UsageDisplayFormatter.FormatStatus(snapshot));
    }

    [TestMethod]
    public void FormatReset_UsesTimeOnlyWithin24HoursWeekdayBeyondAndDashForNull()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var within = now.AddHours(23);
        var beyond = now.AddDays(2);

        Assert.AreEqual($"{within.ToLocalTime():HH:mm} 초기화", UsageDisplayFormatter.FormatReset(within, now));
        var beyondLocal = beyond.ToLocalTime();
        Assert.AreEqual(
            $"{GetKoreanWeekday(beyondLocal.DayOfWeek)} {beyondLocal:HH:mm}",
            UsageDisplayFormatter.FormatReset(beyond, now));
        Assert.AreEqual("--", UsageDisplayFormatter.FormatReset(null, now));
    }

    [TestMethod]
    public void FormatResetAccessible_UsesLocalDateAndTimeOrDash()
    {
        var resetsAt = new DateTimeOffset(2026, 9, 5, 4, 5, 0, TimeSpan.Zero);

        Assert.AreEqual(
            resetsAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            UsageDisplayFormatter.FormatResetAccessible(resetsAt));
        Assert.AreEqual("--", UsageDisplayFormatter.FormatResetAccessible(null));
    }

    [TestMethod]
    public void FormatStatus_MapsEverySupportedStatus()
    {
        var cases = new[]
        {
            (Provider: "codex", Status: ProviderStatus.Ready, Expected: "정상"),
            (Provider: "codex", Status: ProviderStatus.Stale, Expected: "오래됨"),
            (Provider: "codex", Status: ProviderStatus.Disabled, Expected: "모니터링 비활성화"),
            (Provider: "codex", Status: ProviderStatus.NotInstalled, Expected: "Codex CLI 없음"),
            (Provider: "claude", Status: ProviderStatus.NotInstalled, Expected: "Claude Code 없음"),
            (Provider: "codex", Status: ProviderStatus.SignedOut, Expected: "Codex 로그인 필요"),
            (Provider: "claude", Status: ProviderStatus.SignedOut, Expected: "Claude 로그인 필요"),
            (Provider: "codex", Status: ProviderStatus.NetworkError, Expected: "네트워크 오류"),
            (Provider: "codex", Status: ProviderStatus.InvalidResponse, Expected: "응답 오류"),
            (Provider: "codex", Status: ProviderStatus.RateLimited, Expected: "요청 제한"),
            (Provider: "codex", Status: ProviderStatus.Forbidden, Expected: "권한 오류"),
            (Provider: "codex", Status: ProviderStatus.UnknownError, Expected: "알 수 없는 오류")
        };

        foreach (var testCase in cases)
        {
            var snapshot = new UsageSnapshot(
                testCase.Provider,
                testCase.Status,
                Array.Empty<UsageWindow>(),
                DateTimeOffset.UtcNow);
            Assert.AreEqual(testCase.Expected, UsageDisplayFormatter.FormatStatus(snapshot), testCase.Status.ToString());
        }
    }

    [TestMethod]
    public void MaxUsedPercent_ChoosesHighestReadyOrStaleAndReturnsMinusOneWhenUnavailable()
    {
        var snapshots = new[]
        {
            new UsageSnapshot(
                "codex",
                ProviderStatus.Ready,
                new[]
                {
                    CreateWindow("ready-low", 25, TimeSpan.FromHours(5)),
                    CreateWindow("ready-high", 85, TimeSpan.FromDays(7)),
                    CreateWindow("ready-invalid", double.PositiveInfinity, TimeSpan.FromMinutes(1))
                },
                DateTimeOffset.UtcNow),
            new UsageSnapshot(
                "claude",
                ProviderStatus.Stale,
                new[] { CreateWindow("stale-high", 88, TimeSpan.FromHours(5)) },
                DateTimeOffset.UtcNow),
            new UsageSnapshot(
                "disabled",
                ProviderStatus.Disabled,
                new[] { CreateWindow("disabled-high", 100, TimeSpan.FromHours(5)) },
                DateTimeOffset.UtcNow),
            new UsageSnapshot(
                "signed-out",
                ProviderStatus.SignedOut,
                new[] { CreateWindow("signed-out-high", 99, TimeSpan.FromHours(5)) },
                DateTimeOffset.UtcNow)
        };

        Assert.AreEqual(88d, UsageDisplayFormatter.MaxUsedPercent(snapshots));
        Assert.AreEqual(-1d, UsageDisplayFormatter.MaxUsedPercent(Array.Empty<UsageSnapshot>()));
        Assert.AreEqual(
            -1d,
            UsageDisplayFormatter.MaxUsedPercent(
                new[]
                {
                    new UsageSnapshot(
                        "disabled",
                        ProviderStatus.Disabled,
                        new[] { CreateWindow("window", 100, TimeSpan.FromHours(5)) },
                        DateTimeOffset.UtcNow)
                }));
    }

    [TestMethod]
    public void SelectWindow_PrefersOverallScopeForSameDuration()
    {
        var model = CreateWindow("model", 20, TimeSpan.FromHours(5), UsageScope.Model);
        var overall = CreateWindow("overall", 30, TimeSpan.FromHours(5), UsageScope.Overall);
        var snapshot = new UsageSnapshot("codex", ProviderStatus.Ready, new[] { model, overall }, DateTimeOffset.UtcNow);

        var selected = UsageDisplayFormatter.SelectWindow(snapshot, TimeSpan.FromHours(5));

        Assert.AreSame(overall, selected);
    }

    private static UsageWindow CreateWindow(
        string id,
        double usedPercent,
        TimeSpan duration,
        UsageScope scope = UsageScope.Overall) => new(
            id,
            UsageWindowClassifier.GetDisplayName(duration),
            scope,
            usedPercent,
            duration,
        null);

    private static UsageSnapshot CreateSnapshot(
        string providerId,
        string? providerKind,
        string? planType,
        params UsageWindow[] windows) => new(
        providerId,
        ProviderStatus.Ready,
        windows,
        DateTimeOffset.UtcNow,
        ProviderKind: providerKind,
        PlanType: planType);

    private static string GetKoreanWeekday(DayOfWeek dayOfWeek) => dayOfWeek switch
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
