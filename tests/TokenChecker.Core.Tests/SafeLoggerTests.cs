using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class SafeLoggerTests
{
    private TempWorkspace workspace = null!;

    [TestInitialize]
    public void SetUp() => workspace = TempWorkspace.Create();

    [TestCleanup]
    public void TearDown() => workspace.Dispose();

    [TestMethod]
    public void LogProviderFailure_AppendsOneFourFieldLineAndUsesDashForNullCode()
    {
        var logger = new SafeLogger(workspace.GetPath("diagnostics.log"));
        var at = new DateTimeOffset(2026, 9, 4, 12, 34, 56, TimeSpan.Zero);

        logger.LogProviderFailure("codex", ProviderStatus.NetworkError, null, at);

        var line = File.ReadAllLines(logger.Path).Single();
        var fields = line.Split('\t');
        Assert.AreEqual(4, fields.Length);
        Assert.AreEqual(at.ToString("O"), fields[0]);
        Assert.AreEqual("codex", fields[1]);
        Assert.AreEqual(nameof(ProviderStatus.NetworkError), fields[2]);
        Assert.AreEqual("-", fields[3]);
    }

    [TestMethod]
    public void LogProviderFailure_TwoCallsAppendTwoLines()
    {
        var logger = new SafeLogger(workspace.GetPath("diagnostics.log"));
        var firstAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var secondAt = firstAt.AddSeconds(1);

        logger.LogProviderFailure("codex", ProviderStatus.NetworkError, null, firstAt);
        logger.LogProviderFailure("claude", ProviderStatus.Forbidden, 403, secondAt);

        var lines = File.ReadAllLines(logger.Path);
        Assert.AreEqual(2, lines.Length);
        CollectionAssert.AreEqual(
            new[] { firstAt.ToString("O"), "codex", nameof(ProviderStatus.NetworkError), "-" },
            lines[0].Split('\t'));
        CollectionAssert.AreEqual(
            new[] { secondAt.ToString("O"), "claude", nameof(ProviderStatus.Forbidden), "403" },
            lines[1].Split('\t'));
    }

    [TestMethod]
    public void LogProviderFailure_FlattensTabsAndNewlinesInProviderId()
    {
        var logger = new SafeLogger(workspace.GetPath("diagnostics.log"));
        var providerId = "codex\tcli\r\nprovider";

        logger.LogProviderFailure(providerId, ProviderStatus.UnknownError, null, DateTimeOffset.UtcNow);

        var lines = File.ReadAllLines(logger.Path);
        Assert.AreEqual(1, lines.Length);
        var fields = lines[0].Split('\t');
        Assert.AreEqual(4, fields.Length);
        Assert.AreEqual("codex cli  provider", fields[1]);
        Assert.IsFalse(fields[1].Contains('\t'));
        Assert.IsFalse(fields[1].Contains('\r'));
        Assert.IsFalse(fields[1].Contains('\n'));
    }
}
