using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class UsageRefreshCoordinatorPushTests
{
    private TempWorkspace workspace = null!;

    [TestInitialize]
    public void SetUp() => workspace = TempWorkspace.Create();

    [TestCleanup]
    public void TearDown() => workspace.Dispose();

    [TestMethod]
    public async Task ProviderPush_PublishesSnapshotResetsFailureStateAndWritesCache()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var provider = new PushUsageProvider("codex");
        provider.Enqueue(Snapshot(ProviderStatus.NetworkError, now, Array.Empty<UsageWindow>()));
        var cache = new UsageCache(workspace.GetPath("push-cache.json"));
        using var coordinator = new UsageRefreshCoordinator(
            new IUsageProvider[] { provider },
            cache,
            new SafeLogger(workspace.GetPath("push.log")),
            clock);
        var updates = new List<UsageSnapshot>();
        coordinator.SnapshotUpdated += (_, snapshot) => updates.Add(snapshot);

        await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        Assert.AreEqual(ProviderStatus.NetworkError, coordinator.Current.Single().Status);

        var pushed = Snapshot(
            ProviderStatus.Ready,
            now.AddSeconds(1),
            new[] { CreateWindow("five-hour", 42, TimeSpan.FromHours(5)) });
        provider.Push(pushed);

        Assert.AreEqual(ProviderStatus.Ready, coordinator.Current.Single().Status);
        Assert.AreEqual(42, coordinator.Current.Single().Windows.Single().UsedPercent);
        Assert.AreSame(pushed, updates.Last());
        Assert.AreEqual(2, updates.Count);

        var persisted = cache.Load(now).Single();
        Assert.AreEqual(ProviderStatus.Stale, persisted.Status);
        Assert.AreEqual(42, persisted.Windows.Single().UsedPercent);

        provider.Enqueue(Snapshot(
            ProviderStatus.Ready,
            now.AddSeconds(2),
            new[] { CreateWindow("five-hour", 43, TimeSpan.FromHours(5)) }));
        await coordinator.RefreshAsync(manual: false, CancellationToken.None);
        Assert.AreEqual(2, provider.CallCount);
        Assert.AreEqual(43, coordinator.Current.Single().Windows.Single().UsedPercent);
    }

    [TestMethod]
    public void ProviderPush_IsIgnoredWhileProviderIsDisabled()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var provider = new PushUsageProvider("codex");
        using var coordinator = new UsageRefreshCoordinator(
            new IUsageProvider[] { provider },
            new UsageCache(workspace.GetPath("disabled-push-cache.json")),
            new SafeLogger(workspace.GetPath("disabled-push.log")),
            new FakeTimeProvider(now));
        var updateCount = 0;
        coordinator.SnapshotUpdated += (_, _) => updateCount++;

        coordinator.SetEnabled("codex", enabled: false);
        var disabled = coordinator.Current.Single();
        var countAfterDisable = updateCount;
        provider.Push(Snapshot(
            ProviderStatus.Ready,
            now,
            new[] { CreateWindow("five-hour", 25, TimeSpan.FromHours(5)) }));

        Assert.AreEqual(ProviderStatus.Disabled, disabled.Status);
        Assert.AreEqual(ProviderStatus.Disabled, coordinator.Current.Single().Status);
        Assert.AreEqual(countAfterDisable, updateCount);
        Assert.AreEqual(0, provider.CallCount);
    }

    [TestMethod]
    public async Task ProviderPush_DoesNotClearAuthBlockWithoutExplicitLogin()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var provider = new PushUsageProvider("codex");
        provider.Enqueue(Snapshot(ProviderStatus.SignedOut, now, Array.Empty<UsageWindow>()));
        using var coordinator = new UsageRefreshCoordinator(
            new IUsageProvider[] { provider },
            new UsageCache(workspace.GetPath("auth-push-cache.json")),
            new SafeLogger(workspace.GetPath("auth-push.log")),
            new FakeTimeProvider(now));
        await coordinator.RefreshAsync(manual: false, CancellationToken.None);

        provider.Push(Snapshot(
            ProviderStatus.Ready,
            now.AddSeconds(1),
            new[] { CreateWindow("five-hour", 25, TimeSpan.FromHours(5)) }));
        await coordinator.RefreshAsync(manual: true, CancellationToken.None);

        Assert.AreEqual(ProviderStatus.SignedOut, coordinator.Current.Single().Status);
        Assert.AreEqual(1, provider.CallCount);
    }

    [TestMethod]
    public async Task ProviderPush_MissingPlanPreservesEachAccountsOwnConfirmedPlan()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var first = new PushUsageProvider("codex-1");
        var second = new PushUsageProvider("codex-2");
        first.Enqueue(Snapshot(ProviderStatus.Ready, now, Array.Empty<UsageWindow>(), first.Id, "pro"));
        second.Enqueue(Snapshot(ProviderStatus.Ready, now, Array.Empty<UsageWindow>(), second.Id, "plus"));
        using var coordinator = new UsageRefreshCoordinator(
            new IUsageProvider[] { first, second },
            new UsageCache(workspace.GetPath("separate-push-cache.json")),
            new SafeLogger(workspace.GetPath("separate-push.log")),
            new FakeTimeProvider(now));
        await coordinator.RefreshAsync(manual: false, CancellationToken.None);

        first.Push(Snapshot(ProviderStatus.Ready, now.AddSeconds(1), Array.Empty<UsageWindow>(), first.Id));
        second.Push(Snapshot(ProviderStatus.Ready, now.AddSeconds(1), Array.Empty<UsageWindow>(), second.Id));

        Assert.AreEqual("pro", coordinator.Current.Single(snapshot => snapshot.ProviderId == first.Id).PlanType);
        Assert.AreEqual("plus", coordinator.Current.Single(snapshot => snapshot.ProviderId == second.Id).PlanType);
    }

    private static UsageSnapshot Snapshot(
        ProviderStatus status,
        DateTimeOffset retrievedAt,
        IReadOnlyList<UsageWindow> windows,
        string providerId = "codex",
        string? planType = null) =>
        new(providerId, status, windows, retrievedAt, ProviderKind: "codex", PlanType: planType);

    private static UsageWindow CreateWindow(string id, double usedPercent, TimeSpan duration) => new(
        id,
        UsageWindowClassifier.GetDisplayName(duration),
        UsageScope.Overall,
        usedPercent,
        duration,
        null);

    private sealed class PushUsageProvider : IUsageProvider, IPushUsageProvider, IDisposable
    {
        private readonly Queue<UsageSnapshot> responses = new();
        private int callCount;

        public PushUsageProvider(string id) => Id = id;

        public string Id { get; }

        public string? ProviderKind => "codex";

        public int CallCount => Volatile.Read(ref callCount);

        public event EventHandler<UsageSnapshot>? UsageUpdated;

        public void Enqueue(UsageSnapshot snapshot) => responses.Enqueue(snapshot);

        public void Push(UsageSnapshot snapshot) => UsageUpdated?.Invoke(this, snapshot);

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref callCount);
            return Task.FromResult(responses.Dequeue());
        }

        public void Dispose()
        {
        }
    }
}
