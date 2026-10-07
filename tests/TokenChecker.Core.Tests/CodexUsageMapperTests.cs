using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class CodexUsageMapperTests
{
    [TestMethod]
    public void ParsePlanType_ReadsMainCodexProPlan()
    {
        const string json = """
        {
          "rateLimitsByLimitId": {
            "codex": {
              "planType": "pro",
              "primary": { "usedPercent": 1, "windowDurationMins": 10080, "resetsAt": 1789435595 },
              "secondary": null
            }
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual("pro", CodexUsageMapper.ParsePlanType(document.RootElement));
        Assert.AreEqual(TimeSpan.FromDays(7), CodexUsageMapper.Parse(document.RootElement).Single().WindowDuration);
    }

    [TestMethod]
    public void ParsePlanType_UsesOnlyCodexLegacyFallback()
    {
        using var unspecified = JsonDocument.Parse("""{"rateLimits":{"planType":"plus"}}""");
        using var codex = JsonDocument.Parse("""{"rateLimits":{"limitId":"codex","planType":"pro"}}""");
        using var spark = JsonDocument.Parse("""{"rateLimits":{"limitId":"codex_bengalfox","planType":"pro"}}""");

        Assert.AreEqual("plus", CodexUsageMapper.ParsePlanType(unspecified.RootElement));
        Assert.AreEqual("pro", CodexUsageMapper.ParsePlanType(codex.RootElement));
        Assert.IsNull(CodexUsageMapper.ParsePlanType(spark.RootElement));
    }

    [TestMethod]
    public void ParsePlanType_MainCodexWinsOverSparkAndUnknownStringIsPreserved()
    {
        const string json = """
        {
          "rateLimitsByLimitId": {
            "codex_bengalfox": { "planType": "max" },
            "codex": { "planType": "future_plan" }
          },
          "rateLimits": { "planType": "pro" }
        }
        """;
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual("future_plan", CodexUsageMapper.ParsePlanType(document.RootElement));
    }

    [TestMethod]
    public void ParsePlanType_MissingMalformedAndSparkOnlyReturnNull()
    {
        var values = new[]
        {
            "[]",
            "{\"rateLimitsByLimitId\":null}",
            "{\"rateLimitsByLimitId\":{\"codex\":null}}",
            "{\"rateLimitsByLimitId\":{\"codex\":{\"planType\":42}}}",
            "{\"rateLimitsByLimitId\":{\"codex_bengalfox\":{\"planType\":\"pro\"}}}"
        };

        foreach (var json in values)
        {
            using var document = JsonDocument.Parse(json);
            Assert.IsNull(CodexUsageMapper.ParsePlanType(document.RootElement), json);
        }
    }

    [TestMethod]
    public void Parse_MalformedNumericFieldsAreSkippedOrDefaultedWithoutThrowing()
    {
        const string json = """
        {
          "rateLimitsByLimitId": {
            "codex": {
              "primary": { "usedPercent": "bad", "windowDurationMins": 300 },
              "secondary": { "usedPercent": 25, "windowDurationMins": "bad", "resetsAt": 999999999999999 }
            },
            "codex_bengalfox": null
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        var window = CodexUsageMapper.Parse(document.RootElement).Single();

        Assert.AreEqual("codex:secondary", window.Id);
        Assert.AreEqual(TimeSpan.Zero, window.WindowDuration);
        Assert.IsNull(window.ResetsAt);
    }

    [TestMethod]
    public void Parse_UsesMultipleBucketsWhenProvided()
    {
        const string json = """
        {
          "rateLimitsByLimitId": {
            "codex": {
              "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1780000000 },
              "secondary": { "usedPercent": 50, "windowDurationMins": 10080, "resetsAt": 1780600000 }
            },
            "codex_other": {
              "primary": { "usedPercent": 10, "windowDurationMins": 60, "resetsAt": 1780000000 }
            }
          }
        }
        """;
        using var document = JsonDocument.Parse(json);

        var windows = CodexUsageMapper.Parse(document.RootElement);

        Assert.AreEqual(3, windows.Count);
        Assert.AreEqual(UsageScope.Overall, windows.Single(window => window.Id == "codex:primary").Scope);
        Assert.AreEqual("Weekly", windows.Single(window => window.Id == "codex:secondary").DisplayName);
        Assert.AreEqual(UsageScope.Unknown, windows.Single(window => window.Id == "codex_other:primary").Scope);
    }
}
