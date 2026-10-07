using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class ClaudeUsageMapperTests
{
    [TestMethod]
    public void Parse_MapsOverallAndDynamicWindows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(GetFixturePath()));

        var windows = ClaudeUsageMapper.Parse(document.RootElement);

        Assert.AreEqual(3, windows.Count);
        Assert.AreEqual("5 hours", windows.Single(window => window.Id == "five_hour").DisplayName);
        Assert.AreEqual(UsageScope.Overall, windows.Single(window => window.Id == "seven_day").Scope);
        Assert.AreEqual(UsageScope.Unknown, windows.Single(window => window.Id == "nimbus_quill").Scope);
        Assert.IsNull(windows.Single(window => window.Id == "nimbus_quill").ResetsAt);
    }

    [TestMethod]
    public void Parse_SkipsObjectsWithNonNumericUtilization()
    {
        // The live 2026-09 response carries extra_usage.utilization = null plus null provider buckets.
        using var document = JsonDocument.Parse(File.ReadAllText(GetFixturePath("claude-usage-live-shape.json")));

        var windows = ClaudeUsageMapper.Parse(document.RootElement);

        CollectionAssert.AreEquivalent(
            new[] { "five_hour", "seven_day", "nimbus_quill" },
            windows.Select(window => window.Id).ToArray());
        Assert.AreEqual(34d, windows.Single(window => window.Id == "five_hour").UsedPercent);
        Assert.AreEqual(TimeSpan.FromDays(7), windows.Single(window => window.Id == "seven_day").WindowDuration);
    }

    private static string GetFixturePath(string fileName = "claude-usage.json") =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
}
