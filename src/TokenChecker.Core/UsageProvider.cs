namespace TokenChecker.Core;

public enum ProviderStatus
{
    Ready,
    Stale,
    Disabled,
    NotInstalled,
    SignedOut,
    NetworkError,
    InvalidResponse,
    RateLimited,
    Forbidden,
    UnknownError
}

public sealed record UsageSnapshot(
    string ProviderId,
    ProviderStatus Status,
    IReadOnlyList<UsageWindow> Windows,
    DateTimeOffset RetrievedAt,
    string? ErrorSummary = null,
    string? DisplayName = null,
    string? ProviderKind = null,
    string? PlanType = null);

public interface IUsageProvider
{
    string Id { get; }

    string? DisplayName => null;

    string? ProviderKind => null;

    string? PlanType => null;

    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken);
}

public interface IPushUsageProvider
{
    event EventHandler<UsageSnapshot>? UsageUpdated;
}
