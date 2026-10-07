using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class UsageRefreshCoordinatorTests
{
    private TempWorkspace workspace = null!;

    [TestInitialize]
    public void SetUp() => workspace = TempWorkspace.Create();

    [TestCleanup]
    public void TearDown() => workspace.Dispose();

    [TestMethod]
    public void Constructor_UncachedProviderIsSeededAsUnknownErrorBeforeFirstRefresh()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var provider = new FakeUsageProvider("codex");
        var coordinator = CreateCoordinator(provider, clock, "uncached");

        var snapshot = coordinator.Current.Single();

        Assert.AreEqual("codex", snapshot.ProviderId);
        Assert.AreEqual(ProviderStatus.UnknownError, snapshot.Status);
        Assert.AreEqual("조회 전", snapshot.ErrorSummary);
        Assert.AreEqual(now, snapshot.RetrievedAt);
        Assert.AreEqual(0, snapshot.Windows.Count);
    }

    [TestMethod]
    public void Constructor_CachedProviderIsSeededAsStale()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var retrievedAt = now.AddHours(-2);
        var clock = new FakeTimeProvider(now);
        var provider = new FakeUsageProvider("codex");
        var cache = new UsageCache(workspace.GetPath("cached-seed.json"));
        var window = CreateWindow("five-hour", 34, TimeSpan.FromHours(5));
        cache.Save(new[] { Snapshot("codex", ProviderStatus.Ready, retrievedAt, new[] { window }) });

        var coordinator = new UsageRefreshCoordinator(
            new IUsageProvider[] { provider },
            cache,
            new SafeLogger(workspace.GetPath("cached-seed.log")),
            clock);

        var snapshot = coordinator.Current.Single();

        Assert.AreEqual(ProviderStatus.Stale, snapshot.Status);
        Assert.AreEqual(retrievedAt, snapshot.RetrievedAt);
        CollectionAssert.AreEqual(new[] { window }, snapshot.Windows.ToArray());
        Assert.AreEqual(0, provider.CallCount);
    }

    [TestMethod]
    public async Task RefreshAsync_ThrowingProviderDoesNotPreventSiblingReadyResult()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var codex = new FakeUsageProvider("codex");
        codex.EnqueueException(new InvalidOperationException("fake provider failure"));
        var claude = new FakeUsageProvider("claude");
        claude.EnqueueSnapshot(Snapshot("claude", ProviderStatus.Ready, clock.UtcNow, new[] { CreateWindow("five-hour", 25, TimeSpan.FromHours(5)) }));
        var coordinator = new UsageRefreshCoordinator(
            new IUsageProvider[] { codex, claude },
            new UsageCache(workspace.GetPath("isolated-cache.json")),
            new SafeLogger(workspace.GetPath("isolated.log")),
            clock);

        var current = await coordinator.RefreshAsync(manual: false, CancellationToken.None);

        Assert.AreEqual(1, codex.CallCount);
        Assert.AreEqual(1, claude.CallCount);
        Assert.AreEqual(ProviderStatus.UnknownError, current.Single(snapshot => snapshot.ProviderId == "codex").Status);
        Assert.AreEqual(ProviderStatus.Ready, current.Single(snapshot => snapshot.ProviderId == "claude").Status);
    }

    [TestMethod]
    public async Task RefreshAsync_TransientFailureAfterSuccessRepublishesPreviousWindowsAsStale()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var retrievedAt = clock.UtcNow.AddMinutes(-10);
        var provider = new FakeUsageProvider("codex");
        var window = CreateWindow("five-hour", 34, TimeSpan.FromHours(5));
        provider.EnqueueSnapshot(Snapshot("codex", ProviderStatus.Ready, retrievedAt, new[] { window }, planType: "pro"));
        provider.EnqueueSnapshot(Snapshot("codex", ProviderStatus.NetworkError, clock.UtcNow.AddMinutes(1), Array.Empty<UsageWindow>(), "offline"));
        var coordinator = CreateCoordinator(provider, clock, "transient");

        await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var current = await coordinator.RefreshAsync(manual: true, CancellationToken.None);
        var snapshot = current.Single();

        Assert.AreEqual(2, provider.CallCount);
        Assert.AreEqual(ProviderStatus.Stale, snapshot.Status);
        Assert.AreEqual(retrievedAt, snapshot.RetrievedAt);
        Assert.AreEqual("offline", snapshot.ErrorSummary);
        Assert.AreEqual("pro", snapshot.PlanType);
        CollectionAssert.AreEqual(new[] { window }, snapshot.Windows.ToArray());
    }

    [TestMethod]
    public async Task RefreshAsync_ReadySnapshotWithoutPlanClearsPreviousPlanAndCache()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var cache = new UsageCache(workspace.GetPath("plan-clear-cache.json"));
        var provider = new FakeUsageProvider("codex-account", "Codex", "codex");
        provider.EnqueueSnapshot(Snapshot(
            provider.Id,
            ProviderStatus.Ready,
            clock.UtcNow,
            new[] { CreateWindow("weekly", 1, TimeSpan.FromDays(7)) },
            planType: "pro"));
        provider.EnqueueSnapshot(Snapshot(
            provider.Id,
            ProviderStatus.Ready,
            clock.UtcNow.AddMinutes(1),
            new[] { CreateWindow("weekly", 2, TimeSpan.FromDays(7)) }));
        using var coordinator = new UsageRefreshCoordinator(
            new[] { provider },
            cache,
            new SafeLogger(workspace.GetPath("plan-clear.log")),
            clock);

        await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        Assert.AreEqual("pro", coordinator.Current.Single().PlanType);

        clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.RefreshAsync(manual: true, CancellationToken.None);

        Assert.IsNull(coordinator.Current.Single().PlanType);
        Assert.IsNull(cache.Load(clock.UtcNow).Single().PlanType);
    }

    [TestMethod]
    public async Task RefreshAsync_SignedOutAndForbiddenBlockAutomaticAndManualUntilCleared()
    {
        var blockedStatuses = new[] { ProviderStatus.SignedOut, ProviderStatus.Forbidden };
        foreach (var blockedStatus in blockedStatuses)
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
            var provider = new FakeUsageProvider("codex");
            provider.EnqueueSnapshot(Snapshot("codex", blockedStatus, clock.UtcNow, Array.Empty<UsageWindow>()));
            provider.EnqueueSnapshot(Snapshot("codex", ProviderStatus.Ready, clock.UtcNow, new[] { CreateWindow("five-hour", 20, TimeSpan.FromHours(5)) }));
            var coordinator = CreateCoordinator(provider, clock, $"auth-{blockedStatus}");

            var first = await coordinator.RefreshAsync(manual: false, CancellationToken.None);
            Assert.AreEqual(blockedStatus, first.Single().Status);
            Assert.AreEqual(1, provider.CallCount);

            await coordinator.RefreshAsync(manual: false, CancellationToken.None);
            await coordinator.RefreshAsync(manual: true, CancellationToken.None);
            Assert.AreEqual(1, provider.CallCount, blockedStatus.ToString());

            coordinator.ClearAuthBlock("codex");
            var afterClear = await coordinator.RefreshAsync(manual: true, CancellationToken.None);
            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(ProviderStatus.Ready, afterClear.Single().Status);
        }
    }

    [TestMethod]
    public async Task RefreshAsync_ComputedBackoffBlocksAutomaticButManualProceeds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeUsageProvider("codex");
        provider.EnqueueSnapshot(Snapshot("codex", ProviderStatus.NetworkError, clock.UtcNow, Array.Empty<UsageWindow>()));
        provider.EnqueueSnapshot(Snapshot("codex", ProviderStatus.Ready, clock.UtcNow, new[] { CreateWindow("five-hour", 45, TimeSpan.FromHours(5)) }));
        var coordinator = CreateCoordinator(provider, clock, "backoff");

        var first = await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        var blocked = await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        Assert.AreEqual(1, provider.CallCount);
        Assert.AreEqual(ProviderStatus.NetworkError, first.Single().Status);
        Assert.AreEqual(ProviderStatus.NetworkError, blocked.Single().Status);

        var afterManual = await coordinator.RefreshAsync(manual: true, CancellationToken.None);

        Assert.AreEqual(2, provider.CallCount);
        Assert.AreEqual(ProviderStatus.Ready, afterManual.Single().Status);
    }

    [TestMethod]
    public async Task RefreshAsync_ClaudeMinimumIntervalBlocksManualUntil60SecondsPass()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeUsageProvider("claude");
        provider.EnqueueSnapshot(Snapshot("claude", ProviderStatus.NetworkError, clock.UtcNow, Array.Empty<UsageWindow>()));
        provider.EnqueueSnapshot(Snapshot("claude", ProviderStatus.Ready, clock.UtcNow, new[] { CreateWindow("five-hour", 45, TimeSpan.FromHours(5)) }));
        var coordinator = CreateCoordinator(provider, clock, "claude-interval");

        await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        await coordinator.RefreshAsync(manual: true, CancellationToken.None);
        Assert.AreEqual(1, provider.CallCount);

        clock.Advance(TimeSpan.FromSeconds(60));
        var afterInterval = await coordinator.RefreshAsync(manual: true, CancellationToken.None);

        Assert.AreEqual(2, provider.CallCount);
        Assert.AreEqual(ProviderStatus.Ready, afterInterval.Single().Status);
    }

    [TestMethod]
    public async Task RefreshAsync_NewClaudeAccountIdStillUsesMinimumInterval()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeUsageProvider("claude-account-1", "Claude Work", "claude");
        provider.EnqueueSnapshot(Snapshot(provider.Id, ProviderStatus.NetworkError, clock.UtcNow, Array.Empty<UsageWindow>()));
        provider.EnqueueSnapshot(Snapshot(provider.Id, ProviderStatus.Ready, clock.UtcNow, new[] { CreateWindow("five-hour", 45, TimeSpan.FromHours(5)) }));
        var coordinator = CreateCoordinator(provider, clock, "new-claude-interval");

        await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        await coordinator.RefreshAsync(manual: true, CancellationToken.None);
        Assert.AreEqual(1, provider.CallCount);

        clock.Advance(TimeSpan.FromSeconds(60));
        await coordinator.RefreshAsync(manual: true, CancellationToken.None);
        Assert.AreEqual(2, provider.CallCount);
        Assert.AreEqual("Claude Work", coordinator.Current.Single().DisplayName);
        Assert.AreEqual("claude", coordinator.Current.Single().ProviderKind);
    }

    [TestMethod]
    public async Task InheritRefreshState_PreservesAuthFailureAndBlocksRequests()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var cache = new UsageCache(workspace.GetPath("inherited-auth-cache.json"));
        cache.Save(new[]
        {
            new UsageSnapshot(
                "account-1",
                ProviderStatus.Ready,
                new[] { CreateWindow("five-hour", 30, TimeSpan.FromHours(5)) },
                clock.UtcNow.AddMinutes(-1),
                DisplayName: "Old Name",
                ProviderKind: "codex")
        });
        var oldProvider = new FakeUsageProvider("account-1", "Old Name", "codex");
        oldProvider.EnqueueSnapshot(Snapshot("account-1", ProviderStatus.Forbidden, clock.UtcNow, Array.Empty<UsageWindow>()));
        using var oldCoordinator = new UsageRefreshCoordinator(
            new[] { oldProvider },
            cache,
            new SafeLogger(workspace.GetPath("inherited-auth-old.log")),
            clock);
        await oldCoordinator.RefreshAsync(manual: false, CancellationToken.None);

        var newProvider = new FakeUsageProvider("account-1", "New Name", "codex");
        newProvider.EnqueueSnapshot(Snapshot("account-1", ProviderStatus.Ready, clock.UtcNow, Array.Empty<UsageWindow>()));
        using var newCoordinator = new UsageRefreshCoordinator(
            new[] { newProvider },
            cache,
            new SafeLogger(workspace.GetPath("inherited-auth-new.log")),
            clock);
        newCoordinator.InheritRefreshState(oldCoordinator);

        var inherited = newCoordinator.Current.Single();
        Assert.AreEqual(ProviderStatus.Forbidden, inherited.Status);
        Assert.AreEqual(0, inherited.Windows.Count);
        Assert.AreEqual("New Name", inherited.DisplayName);
        await newCoordinator.RefreshAsync(manual: true, CancellationToken.None);
        Assert.AreEqual(0, newProvider.CallCount);

        newCoordinator.SetEnabled("account-1", enabled: false);
        newCoordinator.SetEnabled("account-1", enabled: true);
        Assert.AreEqual(ProviderStatus.Forbidden, newCoordinator.Current.Single().Status);
    }

    [TestMethod]
    public async Task InheritRefreshState_SetEnabledTrueDoesNotClearSoftBackoff()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var oldProvider = new FakeUsageProvider("account-1", "Old Name", "codex");
        oldProvider.EnqueueSnapshot(Snapshot(
            "account-1",
            ProviderStatus.NetworkError,
            clock.UtcNow,
            Array.Empty<UsageWindow>()));
        using var oldCoordinator = CreateCoordinator(oldProvider, clock, "soft-backoff-old");
        await oldCoordinator.RefreshAsync(manual: false, CancellationToken.None);

        var newProvider = new FakeUsageProvider("account-1", "New Name", "codex");
        newProvider.EnqueueSnapshot(Snapshot(
            "account-1",
            ProviderStatus.Ready,
            clock.UtcNow,
            Array.Empty<UsageWindow>()));
        using var newCoordinator = CreateCoordinator(newProvider, clock, "soft-backoff-new");
        newCoordinator.InheritRefreshState(oldCoordinator);
        newCoordinator.SetEnabled("account-1", enabled: true);

        await newCoordinator.RefreshAsync(manual: false, CancellationToken.None);

        Assert.AreEqual(0, newProvider.CallCount);
    }

    [TestMethod]
    public async Task InheritRefreshState_PreservesReadySnapshotAndLastGoodWithNewMetadata()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var window = CreateWindow("five-hour", 42, TimeSpan.FromHours(5));
        var oldProvider = new FakeUsageProvider("account-1", "Old Name", "codex");
        oldProvider.EnqueueSnapshot(Snapshot(
            "account-1",
            ProviderStatus.Ready,
            clock.UtcNow,
            new[] { window }));
        using var oldCoordinator = CreateCoordinator(oldProvider, clock, "ready-state-old");
        await oldCoordinator.RefreshAsync(manual: false, CancellationToken.None);

        var newProvider = new FakeUsageProvider("account-1", "New Name", "codex");
        newProvider.EnqueueSnapshot(Snapshot(
            "account-1",
            ProviderStatus.NetworkError,
            clock.UtcNow.AddMinutes(1),
            Array.Empty<UsageWindow>()));
        using var newCoordinator = CreateCoordinator(newProvider, clock, "ready-state-new");
        newCoordinator.InheritRefreshState(oldCoordinator);

        var inherited = newCoordinator.Current.Single();
        Assert.AreEqual(ProviderStatus.Ready, inherited.Status);
        Assert.AreEqual("New Name", inherited.DisplayName);
        CollectionAssert.AreEqual(new[] { window }, inherited.Windows.ToArray());

        clock.Advance(TimeSpan.FromMinutes(1));
        await newCoordinator.RefreshAsync(manual: true, CancellationToken.None);
        Assert.AreEqual(ProviderStatus.Stale, newCoordinator.Current.Single().Status);
        CollectionAssert.AreEqual(new[] { window }, newCoordinator.Current.Single().Windows.ToArray());
    }

    [TestMethod]
    public async Task SetEnabledFalse_PublishesDisabledAndStopsProviderCalls()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeUsageProvider("codex");
        provider.EnqueueSnapshot(Snapshot("codex", ProviderStatus.Ready, clock.UtcNow, new[] { CreateWindow("five-hour", 30, TimeSpan.FromHours(5)) }));
        var coordinator = CreateCoordinator(provider, clock, "disabled");

        coordinator.SetEnabled("codex", enabled: false);
        var disabled = coordinator.Current.Single();
        Assert.AreEqual(ProviderStatus.Disabled, disabled.Status);
        Assert.AreEqual(0, disabled.Windows.Count);

        await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        await coordinator.RefreshAsync(manual: true, CancellationToken.None);

        Assert.AreEqual(0, provider.CallCount);
        Assert.AreEqual(ProviderStatus.Disabled, coordinator.Current.Single().Status);
    }

    [TestMethod]
    public async Task SetEnabledFalse_PreservesKnownPlan()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeUsageProvider("codex-account", "Codex", "codex");
        provider.EnqueueSnapshot(Snapshot(
            provider.Id,
            ProviderStatus.Ready,
            clock.UtcNow,
            new[] { CreateWindow("weekly", 1, TimeSpan.FromDays(7)) },
            planType: "pro"));
        using var coordinator = CreateCoordinator(provider, clock, "disabled-plan");
        await coordinator.RefreshAsync(manual: false, CancellationToken.None);

        coordinator.SetEnabled(provider.Id, enabled: false);

        Assert.AreEqual(ProviderStatus.Disabled, coordinator.Current.Single().Status);
        Assert.AreEqual("pro", coordinator.Current.Single().PlanType);
    }

    [TestMethod]
    public async Task InheritRefreshState_PreservesPlansByAccountId()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var first = new FakeUsageProvider("codex-1", "First", "codex");
        var second = new FakeUsageProvider("codex-2", "Second", "codex");
        first.EnqueueSnapshot(Snapshot(first.Id, ProviderStatus.Ready, clock.UtcNow, Array.Empty<UsageWindow>(), planType: "pro"));
        second.EnqueueSnapshot(Snapshot(second.Id, ProviderStatus.Ready, clock.UtcNow, Array.Empty<UsageWindow>(), planType: "plus"));
        using var previous = new UsageRefreshCoordinator(
            new IUsageProvider[] { first, second },
            new UsageCache(workspace.GetPath("inherit-plans-old.json")),
            new SafeLogger(workspace.GetPath("inherit-plans-old.log")),
            clock);
        await previous.RefreshAsync(manual: false, CancellationToken.None);

        using var current = new UsageRefreshCoordinator(
            new IUsageProvider[]
            {
                new FakeUsageProvider("codex-1", "First renamed", "codex"),
                new FakeUsageProvider("codex-2", "Second renamed", "codex")
            },
            new UsageCache(workspace.GetPath("inherit-plans-new.json")),
            new SafeLogger(workspace.GetPath("inherit-plans-new.log")),
            clock);
        current.InheritRefreshState(previous);

        Assert.AreEqual("pro", current.Current.Single(snapshot => snapshot.ProviderId == "codex-1").PlanType);
        Assert.AreEqual("plus", current.Current.Single(snapshot => snapshot.ProviderId == "codex-2").PlanType);
    }

    [TestMethod]
    public async Task RefreshAsync_SuccessfulRefreshWritesCacheFile()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var cachePath = workspace.GetPath("successful-cache.json");
        var cache = new UsageCache(cachePath);
        var provider = new FakeUsageProvider("codex");
        var retrievedAt = clock.UtcNow.AddMinutes(-2);
        var window = CreateWindow("five-hour", 34, TimeSpan.FromHours(5));
        provider.EnqueueSnapshot(Snapshot("codex", ProviderStatus.Ready, retrievedAt, new[] { window }));
        var coordinator = new UsageRefreshCoordinator(
            new IUsageProvider[] { provider },
            cache,
            new SafeLogger(workspace.GetPath("successful.log")),
            clock);

        await coordinator.RefreshAsync(manual: false, CancellationToken.None);

        Assert.IsTrue(File.Exists(cachePath));
        var persisted = cache.Load(clock.UtcNow).Single();
        Assert.AreEqual("codex", persisted.ProviderId);
        Assert.AreEqual(ProviderStatus.Stale, persisted.Status);
        Assert.AreEqual(retrievedAt, persisted.RetrievedAt);
        CollectionAssert.AreEqual(new[] { window }, persisted.Windows.ToArray());
    }

    private UsageRefreshCoordinator CreateCoordinator(
        FakeUsageProvider provider,
        FakeTimeProvider clock,
        string name) => new(
        new IUsageProvider[] { provider },
        new UsageCache(workspace.GetPath($"{name}-cache.json")),
        new SafeLogger(workspace.GetPath($"{name}.log")),
        clock);

    private static UsageSnapshot Snapshot(
        string providerId,
        ProviderStatus status,
        DateTimeOffset retrievedAt,
        IReadOnlyList<UsageWindow> windows,
        string? errorSummary = null,
        string? planType = null) => new(providerId, status, windows, retrievedAt, errorSummary, PlanType: planType);

    private static UsageWindow CreateWindow(string id, double usedPercent, TimeSpan duration) => new(
        id,
        UsageWindowClassifier.GetDisplayName(duration),
        UsageScope.Overall,
        usedPercent,
        duration,
        null);
}
