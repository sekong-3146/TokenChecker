using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TokenChecker.Core;

public sealed class ClaudeUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";
    // Undocumented; taken from the installed Claude Code 2.1.286 binary. Re-check after CLI upgrades.
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FallbackTokenLifetime = TimeSpan.FromHours(1);
    // An old token this close to expiry is not sent: a 401 would auth-block the account.
    private static readonly TimeSpan ExpiryGuard = TimeSpan.FromSeconds(60);
    private readonly HttpClient httpClient;
    private readonly string credentialPath;
    private readonly AgentAccount? account;
    // Rotated tokens that could not be written yet; the old refresh token is already spent.
    // Keyed by credential file and shared by all instances, because a settings change rebuilds the providers.
    private static readonly ConcurrentDictionary<string, PendingTokens> PendingByCredentialPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string pendingKey;
    private string? planType;

    public ClaudeUsageProvider(HttpClient httpClient, string? credentialPath = null, AgentAccount? account = null)
    {
        if (account is not null && account.Kind != AgentKind.Claude)
        {
            throw new ArgumentException("Claude 공급자에는 Claude 계정만 사용할 수 있습니다.", nameof(account));
        }

        this.httpClient = httpClient;
        this.account = account?.Clone();
        this.credentialPath = credentialPath ?? (this.account is null
            ? Path.Combine(GetDefaultConfigDirectory(), ".credentials.json")
            : GetCredentialPath(this.account));
        pendingKey = Path.GetFullPath(this.credentialPath);
    }

    public static string GetCredentialPath(AgentAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Path.Combine(account.GetConfigDirectory(), ".credentials.json");
    }

    // Drops kept tokens of accounts that no longer exist, so they do not stay in memory until exit.
    public static void ForgetPendingTokensExcept(IEnumerable<string> activeCredentialPaths)
    {
        ArgumentNullException.ThrowIfNull(activeCredentialPaths);
        var active = new HashSet<string>(
            activeCredentialPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in PendingByCredentialPath.Keys)
        {
            if (!active.Contains(key))
            {
                PendingByCredentialPath.TryRemove(key, out _);
            }
        }
    }

    public string Id => account?.Id ?? "claude";

    public string? DisplayName => account?.Name ?? "Claude";

    public string? ProviderKind => "claude";

    public string? PlanType => planType;

    // The coordinator consumes this value without retaining the response body.
    public TimeSpan? RetryAfter { get; private set; }

    public int? LastHttpStatusCode { get; private set; }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RetryAfter = null;
        LastHttpStatusCode = null;
        var retrievedAt = DateTimeOffset.UtcNow;
        var credentials = await ReadCredentialsAsync(cancellationToken);
        if (PendingByCredentialPath.TryGetValue(pendingKey, out var pending))
        {
            switch (credentials.State)
            {
                case CredentialFileState.Unreadable:
                    // Nothing is known about the file right now; keep working from memory and write later.
                    credentials = UseKeptTokens(credentials, pending);
                    break;
                case CredentialFileState.Missing:
                    // The login was removed; kept tokens must not bring it back.
                    PendingByCredentialPath.TryRemove(pendingKey, out _);
                    break;
                default:
                    switch (TrySaveRefreshedCredentials(pending))
                    {
                        case SaveResult.FileReplaced:
                            // Someone else rewrote the file (a fresh login); its tokens are newer than the kept ones.
                            PendingByCredentialPath.TryRemove(pendingKey, out _);
                            credentials = await ReadCredentialsAsync(cancellationToken);
                            break;
                        case SaveResult.Saved:
                            PendingByCredentialPath.TryRemove(pendingKey, out _);
                            credentials = UseKeptTokens(credentials, pending);
                            break;
                        default:
                            // The file still holds the spent tokens, so the in-memory ones win until a write succeeds.
                            credentials = UseKeptTokens(credentials, pending);
                            break;
                    }

                    break;
            }
        }

        if (credentials.State == CredentialFileState.Unreadable && credentials.AccessToken is null)
        {
            // A locked or half-written file is not a sign-out: report a transient error so the next
            // cycle tries again instead of auth-blocking the account.
            return CreateSnapshot(ProviderStatus.UnknownError, retrievedAt, "자격증명 파일 읽기 실패");
        }

        var token = credentials.AccessToken;
        if (credentials.State != CredentialFileState.Unreadable)
        {
            planType = credentials.PlanType;
        }
        if (token is not null && ShouldRefresh(credentials, retrievedAt))
        {
            // Isolated account folders are never touched by the CLI, so nobody else renews the token.
            var refresh = await RefreshTokenAsync(credentials.RefreshToken!, cancellationToken);
            if (refresh.AccessToken is not null)
            {
                token = refresh.AccessToken;
            }
            else if (credentials.ExpiresAt <= DateTimeOffset.UtcNow + ExpiryGuard)
            {
                // Checked after the attempt: the refresh request itself may have outlived the token.
                // No retry here; the coordinator's backoff or auth block decides what happens next.
                return CreateSnapshot(refresh.Status, retrievedAt, refresh.Summary);
            }
        }

        credentials = default;
        if (token is null)
        {
            return CreateSnapshot(ProviderStatus.SignedOut, retrievedAt, "로그인 필요");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            LastHttpStatusCode = (int)response.StatusCode;
            RetryAfter = ParseRetryAfter(response);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => ProviderStatus.SignedOut,
                    System.Net.HttpStatusCode.Forbidden => ProviderStatus.Forbidden,
                    System.Net.HttpStatusCode.TooManyRequests => ProviderStatus.RateLimited,
                    _ => ProviderStatus.UnknownError
                };
                return CreateSnapshot(status, retrievedAt, $"HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var windows = ClaudeUsageMapper.Parse(document.RootElement);
            return windows.Count == 0
                ? CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt, "응답 오류")
                : CreateSnapshot(ProviderStatus.Ready, retrievedAt, windows: windows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt, "네트워크 오류");
        }
        catch (HttpRequestException)
        {
            return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt, "네트워크 오류");
        }
        catch (JsonException)
        {
            return CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt, "응답 오류");
        }
        catch (InvalidOperationException)
        {
            return CreateSnapshot(ProviderStatus.UnknownError, retrievedAt, "조회 실패");
        }
        finally
        {
            token = null;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var retryAt) && retryAt > DateTimeOffset.UtcNow
            ? retryAt - DateTimeOffset.UtcNow
            : null;
    }

    private async Task<CredentialInfo> ReadCredentialsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(credentialPath))
        {
            return default(CredentialInfo) with { State = CredentialFileState.Missing };
        }

        try
        {
            await using var stream = File.OpenRead(credentialPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var accessToken = oauth.TryGetProperty("accessToken", out var token) &&
                              token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
            var subscriptionType = oauth.TryGetProperty("subscriptionType", out var subscription) &&
                                   subscription.ValueKind == JsonValueKind.String
                ? subscription.GetString()
                : null;
            var refreshToken = oauth.TryGetProperty("refreshToken", out var refresh) &&
                               refresh.ValueKind == JsonValueKind.String
                ? refresh.GetString()
                : null;
            DateTimeOffset? expiresAt = oauth.TryGetProperty("expiresAt", out var expires) &&
                                        expires.ValueKind == JsonValueKind.Number &&
                                        expires.TryGetInt64(out var expiresMilliseconds) &&
                                        expiresMilliseconds > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(expiresMilliseconds)
                : null;
            var rateLimitTier = oauth.TryGetProperty("rateLimitTier", out var tier) &&
                                tier.ValueKind == JsonValueKind.String
                ? tier.GetString()
                : null;
            return new CredentialInfo(
                accessToken,
                CombinePlan(subscriptionType, rateLimitTier),
                string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken,
                expiresAt,
                CredentialFileState.Ok);
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Locked, access denied, or caught mid-write: the content is unknown, not absent.
            return default(CredentialInfo) with { State = CredentialFileState.Unreadable };
        }
    }

    private static CredentialInfo UseKeptTokens(CredentialInfo credentials, PendingTokens pending) => credentials with
    {
        AccessToken = pending.AccessToken,
        RefreshToken = pending.RefreshToken ?? credentials.RefreshToken ?? pending.FileRefreshToken,
        ExpiresAt = pending.ExpiresAt
    };

    // "max" alone hides the multiplier; rateLimitTier (e.g. "default_claude_max_5x") carries it.
    private static string? CombinePlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType))
        {
            return null;
        }

        var plan = subscriptionType.Trim();
        if (!string.Equals(plan, "max", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(rateLimitTier))
        {
            return plan;
        }

        var match = Regex.Match(rateLimitTier.Trim(), @"_max_(\d{1,3})x$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? $"max_{match.Groups[1].Value}x" : plan;
    }

    private bool ShouldRefresh(CredentialInfo credentials, DateTimeOffset now) =>
        account is { UseDefaultCredentials: false } &&
        credentials.RefreshToken is not null &&
        credentials.ExpiresAt is { } expiresAt &&
        expiresAt <= now + RefreshMargin;

    private async Task<RefreshOutcome> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                client_id = OAuthClientId
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            LastHttpStatusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                RetryAfter = ParseRetryAfter(response);
                return (int)response.StatusCode switch
                {
                    400 or 401 or 403 => new RefreshOutcome(null, ProviderStatus.SignedOut, "로그인 필요"),
                    429 => new RefreshOutcome(null, ProviderStatus.RateLimited, "토큰 갱신 요청 제한"),
                    _ => new RefreshOutcome(null, ProviderStatus.UnknownError, $"토큰 갱신 HTTP {(int)response.StatusCode}")
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var accessToken = root.ValueKind == JsonValueKind.Object &&
                              root.TryGetProperty("access_token", out var access) &&
                              access.ValueKind == JsonValueKind.String
                ? access.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new RefreshOutcome(null, ProviderStatus.InvalidResponse, "토큰 갱신 응답 오류");
            }

            var rotatedRefreshToken = root.TryGetProperty("refresh_token", out var rotated) &&
                                      rotated.ValueKind == JsonValueKind.String
                ? rotated.GetString()
                : null;
            var lifetime = root.TryGetProperty("expires_in", out var expiresIn) &&
                           expiresIn.ValueKind == JsonValueKind.Number &&
                           expiresIn.TryGetDouble(out var seconds) &&
                           seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : FallbackTokenLifetime;
            // If an earlier rotation is still unsaved, the file holds the token from before that one.
            var fileRefreshToken = PendingByCredentialPath.TryGetValue(pendingKey, out var unsaved)
                ? unsaved.FileRefreshToken
                : refreshToken;
            var rotatedTokens = new PendingTokens(
                accessToken,
                rotatedRefreshToken,
                DateTimeOffset.UtcNow + lifetime,
                fileRefreshToken);
            // A failed write must not discard tokens the server already rotated; keep them and retry next poll.
            if (TrySaveRefreshedCredentials(rotatedTokens) == SaveResult.Failed)
            {
                PendingByCredentialPath[pendingKey] = rotatedTokens;
            }
            else
            {
                PendingByCredentialPath.TryRemove(pendingKey, out _);
            }

            return new RefreshOutcome(accessToken, ProviderStatus.Ready, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new RefreshOutcome(null, ProviderStatus.NetworkError, "네트워크 오류");
        }
        catch (HttpRequestException)
        {
            return new RefreshOutcome(null, ProviderStatus.NetworkError, "네트워크 오류");
        }
        catch (JsonException)
        {
            return new RefreshOutcome(null, ProviderStatus.InvalidResponse, "토큰 갱신 응답 오류");
        }
    }

    // True while rotated tokens exist only in memory; they are lost if the app exits before a write succeeds.
    public bool HasUnsavedTokens => PendingByCredentialPath.ContainsKey(pendingKey);

    private SaveResult TrySaveRefreshedCredentials(PendingTokens tokens)
    {
        try
        {
            return SaveRefreshedCredentials(tokens) ? SaveResult.Saved : SaveResult.FileReplaced;
        }
        catch (FileNotFoundException)
        {
            return SaveResult.FileReplaced;
        }
        catch (DirectoryNotFoundException)
        {
            return SaveResult.FileReplaced;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return SaveResult.Failed;
        }
    }

    // Rewrites only the token fields; every other field the CLI stored is kept as is.
    // Returns false without writing when the file no longer holds the tokens these replace.
    private bool SaveRefreshedCredentials(PendingTokens tokens)
    {
        if (JsonNode.Parse(File.ReadAllText(credentialPath)) is not JsonObject root ||
            root["claudeAiOauth"] is not JsonObject oauth)
        {
            return false;
        }

        // Checked on the same read that is written back, so a login that landed meanwhile is not overwritten.
        var storedRefreshToken = oauth["refreshToken"] is JsonValue stored && stored.TryGetValue<string>(out var text)
            ? text
            : null;
        if (!string.Equals(storedRefreshToken, tokens.FileRefreshToken, StringComparison.Ordinal) &&
            !(tokens.RefreshToken is not null &&
              string.Equals(storedRefreshToken, tokens.RefreshToken, StringComparison.Ordinal)))
        {
            return false;
        }

        oauth["accessToken"] = tokens.AccessToken;
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            oauth["refreshToken"] = tokens.RefreshToken;
        }

        oauth["expiresAt"] = tokens.ExpiresAt.ToUnixTimeMilliseconds();
        var temporaryPath = $"{credentialPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                root.ToJsonString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, credentialPath, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception)
            {
                // A failed cleanup must not hide the original persistence failure.
            }

            throw;
        }
    }

    private static string GetDefaultConfigDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    private UsageSnapshot CreateSnapshot(
        ProviderStatus status,
        DateTimeOffset retrievedAt,
        string? errorSummary = null,
        IReadOnlyList<UsageWindow>? windows = null) =>
        new(Id, status, windows ?? Array.Empty<UsageWindow>(), retrievedAt, errorSummary, DisplayName, ProviderKind, PlanType);

    private enum CredentialFileState
    {
        Ok,
        Missing,
        Unreadable
    }

    private enum SaveResult
    {
        Saved,
        Failed,
        FileReplaced
    }

    private readonly record struct CredentialInfo(
        string? AccessToken,
        string? PlanType,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        CredentialFileState State);

    private readonly record struct RefreshOutcome(string? AccessToken, ProviderStatus Status, string? Summary);

    // FileRefreshToken is the refresh token the credential file is known to still hold.
    private readonly record struct PendingTokens(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset ExpiresAt,
        string FileRefreshToken);
}
