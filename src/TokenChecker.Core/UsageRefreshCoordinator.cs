namespace TokenChecker.Core;

public sealed class UsageRefreshCoordinator : IDisposable
{
    private static readonly TimeSpan ClaudeMinimumInterval = TimeSpan.FromSeconds(60);
    private readonly IReadOnlyList<IUsageProvider> providers;
    private readonly UsageCache cache;
    private readonly SafeLogger logger;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, ProviderState> stateByProvider;
    private readonly Dictionary<string, UsageSnapshot> lastGoodByProvider;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object sync = new();
    private int disposed;
    private IReadOnlyList<UsageSnapshot> current = Array.Empty<UsageSnapshot>();

    public event EventHandler<UsageSnapshot>? SnapshotUpdated;

    public UsageRefreshCoordinator(
        IEnumerable<IUsageProvider> providers,
        UsageCache? cache = null,
        SafeLogger? logger = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        this.providers = providers.ToArray();
        this.cache = cache ?? new UsageCache();
        this.logger = logger ?? new SafeLogger();
        this.clock = clock ?? TimeProvider.System;
        stateByProvider = new Dictionary<string, ProviderState>(StringComparer.OrdinalIgnoreCase);
        lastGoodByProvider = new Dictionary<string, UsageSnapshot>(StringComparer.OrdinalIgnoreCase);

        var now = this.clock.GetUtcNow();
        IReadOnlyList<UsageSnapshot> cached;
        try
        {
            cached = this.cache.Load(now);
        }
        catch
        {
            cached = Array.Empty<UsageSnapshot>();
        }

        var cachedByProvider = cached
            .GroupBy(snapshot => snapshot.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var initial = new List<UsageSnapshot>(this.providers.Count);
        foreach (var provider in this.providers)
        {
            if (stateByProvider.ContainsKey(provider.Id))
            {
                continue;
            }

            var state = new ProviderState();
            stateByProvider.Add(provider.Id, state);
            if (cachedByProvider.TryGetValue(provider.Id, out var cachedSnapshot))
            {
                var stale = new UsageSnapshot(
                    provider.Id,
                    ProviderStatus.Stale,
                    cachedSnapshot.Windows,
                    cachedSnapshot.RetrievedAt,
                    cachedSnapshot.ErrorSummary,
                    provider.DisplayName ?? cachedSnapshot.DisplayName,
                    provider.ProviderKind ?? cachedSnapshot.ProviderKind,
                    cachedSnapshot.PlanType ?? provider.PlanType);
                lastGoodByProvider[provider.Id] = stale;
                initial.Add(stale);
            }
            else
            {
                initial.Add(new UsageSnapshot(
                    provider.Id,
                    ProviderStatus.UnknownError,
                    Array.Empty<UsageWindow>(),
                    now,
                    "조회 전",
                    provider.DisplayName,
                    provider.ProviderKind,
                    provider.PlanType));
            }
        }
        current = initial.ToArray();
        foreach (var provider in this.providers)
        {
            if (provider is IPushUsageProvider pushProvider)
            {
                pushProvider.UsageUpdated += HandleProviderUsageUpdated;
            }
        }
    }

    public IReadOnlyList<UsageSnapshot> Current => current;

    public void SetEnabled(string providerId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        var now = clock.GetUtcNow();

        lock (sync)
        {
            if (!stateByProvider.TryGetValue(providerId, out var state))
            {
                return;
            }

            if (state.Enabled == enabled)
            {
                return;
            }

            state.Enabled = enabled;
            if (!enabled)
            {
                Publish(new UsageSnapshot(
                    providerId,
                    ProviderStatus.Disabled,
                    Array.Empty<UsageWindow>(),
                    now,
                    DisplayName: GetProvider(providerId)?.DisplayName,
                    ProviderKind: GetProvider(providerId)?.ProviderKind,
                    PlanType: GetKnownPlanType(providerId)));
                return;
            }

            state.NextAllowedAt = DateTimeOffset.MinValue;
            if (state.AuthBlocked && state.AuthFailure is not null)
            {
                Publish(state.AuthFailure);
                return;
            }

            if (lastGoodByProvider.TryGetValue(providerId, out var lastGood))
            {
                Publish(new UsageSnapshot(
                    providerId,
                    ProviderStatus.Stale,
                    lastGood.Windows,
                    lastGood.RetrievedAt,
                    lastGood.ErrorSummary,
                    lastGood.DisplayName,
                    lastGood.ProviderKind,
                    lastGood.PlanType));
            }
            else
            {
                Publish(new UsageSnapshot(
                    providerId,
                    ProviderStatus.UnknownError,
                    Array.Empty<UsageWindow>(),
                    now,
                    "조회 전",
                    GetProvider(providerId)?.DisplayName,
                    GetProvider(providerId)?.ProviderKind,
                    GetKnownPlanType(providerId)));
            }
        }
    }

    // Called after the user performs an explicit re-login so the next refresh is attempted again.
    public void ClearAuthBlock(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);

        lock (sync)
        {
            if (stateByProvider.TryGetValue(providerId, out var state))
            {
                state.AuthBlocked = false;
                state.AuthFailure = null;
                state.NextAllowedAt = DateTimeOffset.MinValue;
            }
        }
    }

    public void InheritRefreshState(UsageRefreshCoordinator previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        Dictionary<string, StateTransfer> transfers;
        lock (previous.sync)
        {
            transfers = previous.stateByProvider.ToDictionary(
                pair => pair.Key,
                pair => new StateTransfer(
                    pair.Value.ConsecutiveFailures,
                    pair.Value.NextAllowedAt,
                    pair.Value.HardWaitUntil,
                    pair.Value.LastRequestAt,
                    pair.Value.AuthBlocked,
                    pair.Value.AuthFailure,
                    previous.current.FirstOrDefault(snapshot =>
                        string.Equals(snapshot.ProviderId, pair.Key, StringComparison.OrdinalIgnoreCase)),
                    previous.lastGoodByProvider.TryGetValue(pair.Key, out var lastGood) ? lastGood : null),
                StringComparer.OrdinalIgnoreCase);
        }

        lock (sync)
        {
            foreach (var pair in stateByProvider)
            {
                if (!transfers.TryGetValue(pair.Key, out var transfer))
                {
                    continue;
                }

                pair.Value.ConsecutiveFailures = transfer.ConsecutiveFailures;
                pair.Value.NextAllowedAt = transfer.NextAllowedAt;
                pair.Value.HardWaitUntil = transfer.HardWaitUntil;
                pair.Value.LastRequestAt = transfer.LastRequestAt;
                pair.Value.AuthBlocked = transfer.AuthBlocked;
                var provider = GetProvider(pair.Key);
                pair.Value.AuthFailure = transfer.AuthFailure is null
                    ? null
                    : NormalizeTransferredSnapshot(pair.Key, provider, transfer.AuthFailure, clearWindows: true);
                if (transfer.LastGood is not null)
                {
                    lastGoodByProvider[pair.Key] = NormalizeTransferredSnapshot(
                        pair.Key,
                        provider,
                        transfer.LastGood,
                        clearWindows: false);
                }

                if (!pair.Value.Enabled)
                {
                    continue;
                }

                if (pair.Value.AuthBlocked && pair.Value.AuthFailure is not null)
                {
                    Publish(pair.Value.AuthFailure);
                }
                else if (transfer.CurrentSnapshot is { Status: not ProviderStatus.Disabled } currentSnapshot)
                {
                    Publish(NormalizeTransferredSnapshot(
                        pair.Key,
                        provider,
                        currentSnapshot,
                        clearWindows: false));
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        SnapshotUpdated = null;

        foreach (var provider in providers)
        {
            if (provider is IPushUsageProvider pushProvider)
            {
                try
                {
                    pushProvider.UsageUpdated -= HandleProviderUsageUpdated;
                }
                catch (Exception)
                {
                }
            }
        }

        foreach (var provider in providers)
        {
            try
            {
                if (provider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                else if (provider is IAsyncDisposable asyncDisposable)
                {
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    public async Task<IReadOnlyList<UsageSnapshot>> RefreshAsync(bool manual, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(UsageRefreshCoordinator));
        }

        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref disposed) != 0)
        {
            refreshGate.Release();
            throw new ObjectDisposedException(nameof(UsageRefreshCoordinator));
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            var work = new List<(IUsageProvider Provider, ProviderState State)>();

            lock (sync)
            {
                foreach (var provider in providers)
                {
                    if (!stateByProvider.TryGetValue(provider.Id, out var state) || !state.Enabled)
                    {
                        continue;
                    }

                    if (manual)
                    {
                        // Explicit refreshes bypass only the coordinator's computed backoff.
                        state.NextAllowedAt = DateTimeOffset.MinValue;
                    }

                    if (IsBlocked(provider, state, manual, now))
                    {
                        continue;
                    }

                    state.LastRequestAt = now;
                    work.Add((provider, state));
                }
            }

            if (work.Count == 0)
            {
                return Current;
            }

            var results = await Task.WhenAll(
                work.Select(item => RefreshProviderAsync(item.Provider, cancellationToken)))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var failures = new List<FailureLog>();
            var successful = false;
            lock (sync)
            {
                var publishedAt = clock.GetUtcNow();
                foreach (var result in results)
                {
                    var state = stateByProvider[result.ProviderId];
                    if (!state.Enabled)
                    {
                        continue;
                    }

                    if (result.Snapshot.Status == ProviderStatus.Ready)
                    {
                        state.ConsecutiveFailures = 0;
                        state.NextAllowedAt = DateTimeOffset.MinValue;
                        state.HardWaitUntil = DateTimeOffset.MinValue;
                        state.AuthBlocked = false;
                        state.AuthFailure = null;
                        var snapshot = EnsureProviderId(result.ProviderId, result.Snapshot);
                        lastGoodByProvider[result.ProviderId] = snapshot;
                        Publish(snapshot);
                        successful = true;
                        continue;
                    }

                    var snapshotFailure = EnsureKnownPlanType(
                        result.ProviderId,
                        EnsureProviderId(result.ProviderId, result.Snapshot));
                    var summary = GetErrorSummary(snapshotFailure);
                    var previous = TryGetPreviousGood(result.ProviderId);
                    var isTransient = IsTransient(snapshotFailure.Status);
                    if (snapshotFailure.Status is ProviderStatus.SignedOut or ProviderStatus.Forbidden)
                    {
                        state.AuthBlocked = true;
                        state.AuthFailure = snapshotFailure with
                        {
                            Windows = Array.Empty<UsageWindow>(),
                            ErrorSummary = summary
                        };
                        state.ConsecutiveFailures = 0;
                        state.NextAllowedAt = DateTimeOffset.MinValue;
                        Publish(state.AuthFailure);
                    }
                    else
                    {
                        state.AuthBlocked = false;
                        state.AuthFailure = null;
                        state.ConsecutiveFailures++;
                        state.NextAllowedAt = publishedAt + BackoffSchedule.GetDelay(state.ConsecutiveFailures);
                        if (result.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
                        {
                            var retryUntil = publishedAt + retryAfter;
                            if (retryUntil > state.HardWaitUntil)
                            {
                                state.HardWaitUntil = retryUntil;
                            }
                        }

                        if (isTransient && previous is not null)
                        {
                            Publish(new UsageSnapshot(
                                result.ProviderId,
                                ProviderStatus.Stale,
                                previous.Windows,
                                previous.RetrievedAt,
                                summary,
                                previous.DisplayName,
                                previous.ProviderKind,
                                previous.PlanType));
                        }
                        else
                        {
                            Publish(snapshotFailure with { ErrorSummary = summary });
                        }
                    }

                    failures.Add(new FailureLog(
                        result.ProviderId,
                        snapshotFailure.Status,
                        result.HttpStatusCode,
                        publishedAt));
                }
            }

            foreach (var failure in failures)
            {
                logger.LogProviderFailure(failure.ProviderId, failure.Status, failure.HttpStatusCode, failure.At);
            }

            if (successful)
            {
                UsageSnapshot[] cacheValues;
                lock (sync)
                {
                    cacheValues = lastGoodByProvider.Values.ToArray();
                }

                cache.Save(cacheValues);
            }

            return Current;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<ProviderResult> RefreshProviderAsync(
        IUsageProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await provider.GetUsageAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                snapshot = new UsageSnapshot(
                    provider.Id,
                    ProviderStatus.UnknownError,
                    Array.Empty<UsageWindow>(),
                    clock.GetUtcNow(),
                    "조회 실패",
                    provider.DisplayName,
                    provider.ProviderKind,
                    provider.PlanType);
            }

            return new ProviderResult(
                provider.Id,
                EnsureProviderMetadata(provider, snapshot),
                GetRetryAfter(provider),
                GetHttpStatusCode(provider));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProviderResult(
                provider.Id,
                new UsageSnapshot(
                    provider.Id,
                    ProviderStatus.NetworkError,
                    Array.Empty<UsageWindow>(),
                    clock.GetUtcNow(),
                    "네트워크 오류",
                    provider.DisplayName,
                    provider.ProviderKind,
                    provider.PlanType),
                GetRetryAfter(provider),
                GetHttpStatusCode(provider));
        }
        catch (Exception)
        {
            return new ProviderResult(
                provider.Id,
                new UsageSnapshot(
                    provider.Id,
                    ProviderStatus.UnknownError,
                    Array.Empty<UsageWindow>(),
                    clock.GetUtcNow(),
                    "조회 실패",
                    provider.DisplayName,
                    provider.ProviderKind,
                    provider.PlanType),
                GetRetryAfter(provider),
                GetHttpStatusCode(provider));
        }
    }

    private void HandleProviderUsageUpdated(object? sender, UsageSnapshot snapshot)
    {
        if (snapshot is null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var sourceProvider = sender as IUsageProvider;
        var providerId = sourceProvider?.Id ?? snapshot.ProviderId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        UsageSnapshot[]? cacheValues = null;
        lock (sync)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                !stateByProvider.TryGetValue(providerId, out var state) ||
                !state.Enabled ||
                state.AuthBlocked ||
                snapshot.Status != ProviderStatus.Ready)
            {
                return;
            }
            var metadataProvider = sourceProvider ?? GetProvider(providerId);
            var published = metadataProvider is null
                ? EnsureProviderId(providerId, snapshot)
                : EnsureProviderMetadata(metadataProvider, snapshot);
            published = EnsureKnownPlanType(providerId, published);
            state.LastRequestAt = clock.GetUtcNow();
            state.ConsecutiveFailures = 0;
            state.NextAllowedAt = DateTimeOffset.MinValue;
            state.HardWaitUntil = DateTimeOffset.MinValue;
            state.AuthBlocked = false;
            state.AuthFailure = null;
            lastGoodByProvider[providerId] = published;
            Publish(published);
            cacheValues = lastGoodByProvider.Values.ToArray();
        }

        cache.Save(cacheValues);
    }

    private static bool IsTransient(ProviderStatus status) => status is
        ProviderStatus.NetworkError or
        ProviderStatus.InvalidResponse or
        ProviderStatus.RateLimited or
        ProviderStatus.UnknownError;

    private static bool IsClaude(IUsageProvider provider) =>
        provider is ClaudeUsageProvider ||
        string.Equals(provider.ProviderKind, "claude", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider.Id, "claude", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlocked(IUsageProvider provider, ProviderState state, bool manual, DateTimeOffset now)
    {
        if (state.HardWaitUntil > now)
        {
            return true;
        }

        // Plan 7.2: auth/permission waits are not bypassed by a manual refresh.
        if (state.AuthBlocked)
        {
            return true;
        }

        if (IsClaude(provider) && state.LastRequestAt is { } lastRequest &&
            now < lastRequest + ClaudeMinimumInterval)
        {
            return true;
        }

        return !manual && state.NextAllowedAt > now;
    }

    private UsageSnapshot? TryGetPreviousGood(string providerId)
    {
        return lastGoodByProvider.TryGetValue(providerId, out var snapshot) ? snapshot : null;
    }

    private string? GetKnownPlanType(string providerId)
    {
        var providerPlanType = GetProvider(providerId)?.PlanType;
        if (!string.IsNullOrWhiteSpace(providerPlanType))
        {
            return providerPlanType;
        }

        if (lastGoodByProvider.TryGetValue(providerId, out var lastGood) &&
            !string.IsNullOrWhiteSpace(lastGood.PlanType))
        {
            return lastGood.PlanType;
        }

        return current.FirstOrDefault(snapshot =>
            string.Equals(snapshot.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))?.PlanType;
    }

    private UsageSnapshot EnsureKnownPlanType(string providerId, UsageSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.PlanType))
        {
            return snapshot;
        }

        var knownPlanType = GetKnownPlanType(providerId);
        return knownPlanType is null ? snapshot : snapshot with { PlanType = knownPlanType };
    }

    private void Publish(UsageSnapshot snapshot)
    {
        var next = Current.ToArray();
        for (var index = 0; index < next.Length; index++)
        {
            if (string.Equals(next[index].ProviderId, snapshot.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                next[index] = snapshot;
                current = next;
                var handlers = SnapshotUpdated;
                if (handlers is null)
                {
                    return;
                }

                foreach (EventHandler<UsageSnapshot> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, snapshot);
                    }
                    catch (Exception)
                    {
                    }
                }

                return;
            }
        }
    }

    private static UsageSnapshot EnsureProviderId(string providerId, UsageSnapshot snapshot)
    {
        return string.Equals(providerId, snapshot.ProviderId, StringComparison.Ordinal)
            ? snapshot
            : snapshot with { ProviderId = providerId };
    }

    private static UsageSnapshot EnsureProviderMetadata(IUsageProvider provider, UsageSnapshot snapshot)
    {
        var normalized = EnsureProviderId(provider.Id, snapshot);
        var displayName = provider.DisplayName ?? normalized.DisplayName;
        var providerKind = provider.ProviderKind ?? normalized.ProviderKind;
        var planType = normalized.PlanType ?? provider.PlanType;
        if (string.Equals(displayName, normalized.DisplayName, StringComparison.Ordinal) &&
            string.Equals(providerKind, normalized.ProviderKind, StringComparison.Ordinal) &&
            string.Equals(planType, normalized.PlanType, StringComparison.Ordinal))
        {
            return normalized;
        }

        return normalized with
        {
            DisplayName = displayName,
            ProviderKind = providerKind,
            PlanType = planType
        };
    }

    private IUsageProvider? GetProvider(string providerId) => providers.FirstOrDefault(provider =>
        string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));

    private static UsageSnapshot NormalizeTransferredSnapshot(
        string providerId,
        IUsageProvider? provider,
        UsageSnapshot snapshot,
        bool clearWindows) => snapshot with
    {
        ProviderId = providerId,
        Windows = clearWindows ? Array.Empty<UsageWindow>() : snapshot.Windows,
        DisplayName = provider?.DisplayName ?? snapshot.DisplayName,
        ProviderKind = provider?.ProviderKind ?? snapshot.ProviderKind
    };

    private static string GetErrorSummary(UsageSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorSummary))
        {
            return snapshot.ErrorSummary!;
        }

        return snapshot.Status switch
        {
            ProviderStatus.NotInstalled => "설치되지 않음",
            ProviderStatus.SignedOut => "로그인 필요",
            ProviderStatus.NetworkError => "네트워크 오류",
            ProviderStatus.InvalidResponse => "응답 오류",
            ProviderStatus.RateLimited => "요청 제한",
            ProviderStatus.Forbidden => "권한 오류",
            _ => "조회 실패"
        };
    }

    private static TimeSpan? GetRetryAfter(IUsageProvider provider) => provider is ClaudeUsageProvider claude
        ? claude.RetryAfter
        : null;

    private static int? GetHttpStatusCode(IUsageProvider provider) => provider is ClaudeUsageProvider claude
        ? claude.LastHttpStatusCode
        : null;

    private sealed class ProviderState
    {
        public bool Enabled { get; set; } = true;

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset NextAllowedAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset HardWaitUntil { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset? LastRequestAt { get; set; }

        public bool AuthBlocked { get; set; }

        public UsageSnapshot? AuthFailure { get; set; }
    }

    private sealed record ProviderResult(
        string ProviderId,
        UsageSnapshot Snapshot,
        TimeSpan? RetryAfter,
        int? HttpStatusCode);

    private sealed record FailureLog(
        string ProviderId,
        ProviderStatus Status,
        int? HttpStatusCode,
        DateTimeOffset At);

    private sealed record StateTransfer(
        int ConsecutiveFailures,
        DateTimeOffset NextAllowedAt,
        DateTimeOffset HardWaitUntil,
        DateTimeOffset? LastRequestAt,
        bool AuthBlocked,
        UsageSnapshot? AuthFailure,
        UsageSnapshot? CurrentSnapshot,
        UsageSnapshot? LastGood);
}
