using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

internal sealed class FakeUsageProvider : IUsageProvider
{
    private readonly object sync = new();
    private readonly Queue<Func<Task<UsageSnapshot>>> responses = new();
    private int callCount;

    public FakeUsageProvider(string id, string? displayName = null, string? providerKind = null)
    {
        Id = id;
        DisplayName = displayName;
        ProviderKind = providerKind;
    }

    public string Id { get; }

    public string? DisplayName { get; }

    public string? ProviderKind { get; }

    public int CallCount => Volatile.Read(ref callCount);

    public void EnqueueSnapshot(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            responses.Enqueue(() => Task.FromResult(snapshot));
        }
    }

    public void EnqueueException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (sync)
        {
            responses.Enqueue(() => Task.FromException<UsageSnapshot>(exception));
        }
    }

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);

        Func<Task<UsageSnapshot>> response;
        lock (sync)
        {
            response = responses.Count == 0
                ? () => Task.FromException<UsageSnapshot>(new InvalidOperationException("No queued response."))
                : responses.Dequeue();
        }

        return response();
    }
}
