using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class UsageCacheTests
{
    private TempWorkspace workspace = null!;

    [TestInitialize]
    public void SetUp() => workspace = TempWorkspace.Create();

    [TestCleanup]
    public void TearDown() => workspace.Dispose();

    [TestMethod]
    public void SaveLoad_RoundTripsWindowsAsStaleWithOriginalRetrievedAt()
    {
        var cache = new UsageCache(workspace.GetPath("usage-cache.json"));
        var retrievedAt = new DateTimeOffset(2026, 9, 4, 8, 30, 0, TimeSpan.Zero);
        var window = new UsageWindow(
            "codex:primary",
            "5 hours",
            UsageScope.Overall,
            34,
            TimeSpan.FromHours(5),
            retrievedAt.AddHours(2));
        var snapshot = new UsageSnapshot(
            "account-1",
            ProviderStatus.Ready,
            new[] { window },
            retrievedAt,
            DisplayName: "업무용",
            ProviderKind: "codex",
            PlanType: "pro");

        cache.Save(new[] { snapshot });
        var loaded = cache.Load(retrievedAt.AddHours(1));

        var actual = loaded.Single();
        Assert.AreEqual("account-1", actual.ProviderId);
        Assert.AreEqual(ProviderStatus.Stale, actual.Status);
        Assert.AreEqual(retrievedAt, actual.RetrievedAt);
        Assert.AreEqual("업무용", actual.DisplayName);
        Assert.AreEqual("codex", actual.ProviderKind);
        Assert.AreEqual("pro", actual.PlanType);
        CollectionAssert.AreEqual(snapshot.Windows.ToArray(), actual.Windows.ToArray());
    }

    [TestMethod]
    public void Load_DropsEntriesOlderThan48HoursAndKeepsFreshSibling()
    {
        var cache = new UsageCache(workspace.GetPath("usage-cache.json"));
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var old = new UsageSnapshot(
            "codex",
            ProviderStatus.Ready,
            new[] { CreateWindow("old", 10) },
            now - UsageCache.MaxAge - TimeSpan.FromMinutes(1));
        var fresh = new UsageSnapshot(
            "claude",
            ProviderStatus.Ready,
            new[] { CreateWindow("fresh", 20) },
            now - TimeSpan.FromHours(1));

        cache.Save(new[] { old, fresh });
        var loaded = cache.Load(now);

        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("claude", loaded[0].ProviderId);
        Assert.AreEqual(ProviderStatus.Stale, loaded[0].Status);
    }

    [TestMethod]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        var cache = new UsageCache(workspace.GetPath("usage-cache.json"));
        File.WriteAllText(cache.Path, "not valid JSON");

        Assert.AreEqual(0, cache.Load(DateTimeOffset.UtcNow).Count);
    }

    [TestMethod]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var cache = new UsageCache(workspace.GetPath("usage-cache.json"));

        Assert.AreEqual(0, cache.Load(DateTimeOffset.UtcNow).Count);
    }

    [TestMethod]
    public void SaveLoad_KeepsMultipleSameKindAccountsSeparatedById()
    {
        var cache = new UsageCache(workspace.GetPath("usage-cache.json"));
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        cache.Save(new[]
        {
            new UsageSnapshot(
                "codex-account-1",
                ProviderStatus.Ready,
                new[] { CreateWindow("first", 10) },
                now,
                DisplayName: "First",
                ProviderKind: "codex"),
            new UsageSnapshot(
                "codex-account-2",
                ProviderStatus.Ready,
                new[] { CreateWindow("second", 20) },
                now,
                DisplayName: "Second",
                ProviderKind: "codex")
        });

        var loaded = cache.Load(now);

        CollectionAssert.AreEquivalent(
            new[] { "codex-account-1", "codex-account-2" },
            loaded.Select(snapshot => snapshot.ProviderId).ToArray());
        Assert.AreEqual(10, loaded.Single(snapshot => snapshot.ProviderId == "codex-account-1").Windows.Single().UsedPercent);
        Assert.AreEqual(20, loaded.Single(snapshot => snapshot.ProviderId == "codex-account-2").Windows.Single().UsedPercent);
    }

    [TestMethod]
    public void Save_PersistsOnlySafeExpectedProperties()
    {
        var cache = new UsageCache(workspace.GetPath("usage-cache.json"));
        var snapshot = new UsageSnapshot(
            "codex",
            ProviderStatus.Ready,
            new[]
            {
                new UsageWindow(
                    "primary",
                    "5 hours",
                    UsageScope.Overall,
                    34,
                    TimeSpan.FromHours(5),
                    new DateTimeOffset(2026, 9, 4, 15, 0, 0, TimeSpan.Zero))
            },
            new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero));

        cache.Save(new[] { snapshot });
        var json = File.ReadAllText(cache.Path);
        var lowerJson = json.ToLowerInvariant();
        Assert.IsFalse(lowerJson.Contains("token", StringComparison.Ordinal));
        Assert.IsFalse(lowerJson.Contains("authorization", StringComparison.Ordinal));
        Assert.IsFalse(lowerJson.Contains("accesstoken", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.EnumerateArray().Single();
        CollectionAssert.AreEquivalent(
            new[] { "ProviderId", "DisplayName", "ProviderKind", "PlanType", "Windows", "RetrievedAt" },
            entry.EnumerateObject().Select(property => property.Name).ToArray());
        var window = entry.GetProperty("Windows").EnumerateArray().Single();
        CollectionAssert.AreEquivalent(
            new[] { "Id", "DisplayName", "Scope", "UsedPercent", "WindowDuration", "ResetsAt" },
            window.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void Load_OldCacheWithoutPlanTypeRemainsCompatible()
    {
        var cache = new UsageCache(workspace.GetPath("old-cache.json"));
        File.WriteAllText(
            cache.Path,
            """
            [{
              "ProviderId": "codex",
              "Windows": [],
              "RetrievedAt": "2026-09-04T12:00:00+00:00"
            }]
            """);

        var loaded = cache.Load(new DateTimeOffset(2026, 9, 4, 13, 0, 0, TimeSpan.Zero)).Single();

        Assert.IsNull(loaded.PlanType);
        Assert.AreEqual(ProviderStatus.Stale, loaded.Status);
    }

    private static UsageWindow CreateWindow(string id, double usedPercent) => new(
        id,
        "5 hours",
        UsageScope.Overall,
        usedPercent,
        TimeSpan.FromHours(5),
        null);
}
