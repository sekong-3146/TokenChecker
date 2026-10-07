using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class ClaudeUsageProviderTests
{
    private TempWorkspace workspace = null!;

    [TestInitialize]
    public void SetUp() => workspace = TempWorkspace.Create();

    [TestCleanup]
    public void TearDown() => workspace.Dispose();

    [TestMethod]
    public async Task GetUsageAsync_AccountUsesUniqueIdMetadataAndExplicitCredentialFile()
    {
        var credentialPath = WriteCredentials("pro");
        var handler = new QueueHandler(CreateUsageResponse());
        using var client = new HttpClient(handler);
        var account = CreateAccount("claude-account-1", "Claude Work");
        var provider = new ClaudeUsageProvider(client, credentialPath, account);

        var snapshot = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, snapshot.Status);
        Assert.AreEqual(account.Id, snapshot.ProviderId);
        Assert.AreEqual(account.Name, snapshot.DisplayName);
        Assert.AreEqual("claude", snapshot.ProviderKind);
        Assert.AreEqual("pro", snapshot.PlanType);
        Assert.AreEqual("Bearer", handler.LastAuthorizationScheme);
    }

    [TestMethod]
    public async Task GetUsageAsync_MissingOrMalformedSubscriptionTypeReturnsNull()
    {
        foreach (var subscriptionProperty in new[] { string.Empty, ",\"subscriptionType\":42" })
        {
            var credentialPath = WriteCredentialsRaw(subscriptionProperty);
            var handler = new QueueHandler(CreateUsageResponse());
            using var client = new HttpClient(handler);
            var provider = new ClaudeUsageProvider(client, credentialPath);

            var snapshot = await provider.GetUsageAsync(CancellationToken.None);

            Assert.AreEqual(ProviderStatus.Ready, snapshot.Status);
            Assert.IsNull(snapshot.PlanType);
        }
    }

    [TestMethod]
    public async Task GetUsageAsync_MaxPlanCarriesMultiplierFromRateLimitTier()
    {
        var cases = new[]
        {
            (Properties: ",\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_5x\"", Expected: "max_5x"),
            (Properties: ",\"subscriptionType\":\"max\",\"rateLimitTier\":\"default_claude_max_20x\"", Expected: "max_20x"),
            (Properties: ",\"subscriptionType\":\"max\",\"rateLimitTier\":\"something_else\"", Expected: "max"),
            (Properties: ",\"subscriptionType\":\"max\",\"rateLimitTier\":7", Expected: "max"),
            (Properties: ",\"subscriptionType\":\"pro\",\"rateLimitTier\":\"default_claude_max_5x\"", Expected: "pro")
        };

        foreach (var item in cases)
        {
            var credentialPath = WriteCredentialsRaw(item.Properties);
            using var client = new HttpClient(new QueueHandler(CreateUsageResponse()));
            var provider = new ClaudeUsageProvider(client, credentialPath);

            var snapshot = await provider.GetUsageAsync(CancellationToken.None);

            Assert.AreEqual(item.Expected, snapshot.PlanType);
        }
    }

    [TestMethod]
    public async Task InheritRefreshState_PreservesClaudeRetryAfterForAccountId()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var credentialPath = WriteCredentials();
        var account = CreateAccount("claude-account-1", "Claude Work");
        var oldHandler = new QueueHandler(CreateRateLimitedResponse());
        using var oldClient = new HttpClient(oldHandler);
        using var oldCoordinator = CreateCoordinator(
            new ClaudeUsageProvider(oldClient, credentialPath, account),
            clock,
            "claude-retry-old");
        await oldCoordinator.RefreshAsync(manual: false, CancellationToken.None);

        var newHandler = new QueueHandler(CreateUsageResponse());
        using var newClient = new HttpClient(newHandler);
        using var newCoordinator = CreateCoordinator(
            new ClaudeUsageProvider(newClient, credentialPath, account),
            clock,
            "claude-retry-new");
        newCoordinator.InheritRefreshState(oldCoordinator);

        clock.Advance(TimeSpan.FromSeconds(60));
        await newCoordinator.RefreshAsync(manual: true, CancellationToken.None);
        Assert.AreEqual(0, newHandler.CallCount);

        clock.Advance(TimeSpan.FromSeconds(60));
        await newCoordinator.RefreshAsync(manual: true, CancellationToken.None);
        Assert.AreEqual(1, newHandler.CallCount);
        Assert.AreEqual(ProviderStatus.Ready, newCoordinator.Current.Single().Status);
    }

    [TestMethod]
    public async Task GetUsageAsync_ExpiredIsolatedAccountRefreshesTokenAndRewritesCredentialFile()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var handler = new QueueHandler(
            CreateTokenResponse("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":28800}"),
            CreateUsageResponse());
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));

        var before = DateTimeOffset.UtcNow;
        var snapshot = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, snapshot.Status);
        Assert.AreEqual("max", snapshot.PlanType);
        Assert.AreEqual(2, handler.CallCount);
        Assert.AreEqual("POST https://platform.claude.com/v1/oauth/token", handler.Requests[0]);
        StringAssert.Contains(handler.Bodies[0], "\"grant_type\":\"refresh_token\"");
        StringAssert.Contains(handler.Bodies[0], "\"refresh_token\":\"old-refresh\"");
        Assert.AreEqual("new-access", handler.LastAuthorizationParameter);

        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(credentialPath));
        var oauth = saved.RootElement.GetProperty("claudeAiOauth");
        Assert.AreEqual("new-access", oauth.GetProperty("accessToken").GetString());
        Assert.AreEqual("new-refresh", oauth.GetProperty("refreshToken").GetString());
        Assert.AreEqual("max", oauth.GetProperty("subscriptionType").GetString());
        Assert.AreEqual("user:profile", oauth.GetProperty("scopes")[0].GetString());
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(oauth.GetProperty("expiresAt").GetInt64());
        Assert.IsTrue(expiresAt >= before.AddHours(8).AddSeconds(-1) && expiresAt <= DateTimeOffset.UtcNow.AddHours(8));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(credentialPath)!, "*.tmp").Length);
    }

    [TestMethod]
    public async Task GetUsageAsync_RejectedRefreshReturnsSignedOutWithoutUsageRequestOrFileChange()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var original = File.ReadAllText(credentialPath);
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));

        var snapshot = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.SignedOut, snapshot.Status);
        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual(400, provider.LastHttpStatusCode);
        Assert.AreEqual(original, File.ReadAllText(credentialPath));
    }

    [TestMethod]
    public async Task GetUsageAsync_DoesNotRefreshValidTokenOrDefaultCredentialAccount()
    {
        var validPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(2));
        var validHandler = new QueueHandler(CreateUsageResponse());
        using var validClient = new HttpClient(validHandler);
        var isolated = new ClaudeUsageProvider(validClient, validPath, CreateAccount("claude-account-1", "Claude Work"));
        Assert.AreEqual(ProviderStatus.Ready, (await isolated.GetUsageAsync(CancellationToken.None)).Status);
        Assert.AreEqual(1, validHandler.CallCount);
        Assert.AreEqual("old-access", validHandler.LastAuthorizationParameter);

        var expiredPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var defaultHandler = new QueueHandler(CreateUsageResponse());
        using var defaultClient = new HttpClient(defaultHandler);
        var defaultAccount = CreateAccount("claude", "Claude");
        defaultAccount.UseDefaultCredentials = true;
        var shared = new ClaudeUsageProvider(defaultClient, expiredPath, defaultAccount);
        await shared.GetUsageAsync(CancellationToken.None);
        Assert.AreEqual(1, defaultHandler.CallCount);
        Assert.AreEqual("GET https://api.anthropic.com/api/oauth/usage", defaultHandler.Requests[0]);
    }

    [TestMethod]
    public async Task GetUsageAsync_RefreshNetworkFailureIsTransientWhenTokenExpired()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));

        var snapshot = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.UnknownError, snapshot.Status);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task GetUsageAsync_KeepsRotatedTokensWhenCredentialFileCannotBeWrittenAndSavesOnNextCall()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var original = File.ReadAllText(credentialPath);
        var handler = new QueueHandler(
            CreateTokenResponse("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":28800}"),
            CreateUsageResponse(),
            CreateUsageResponse());
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));

        // Replacing a read-only file fails, like a transient lock would.
        File.SetAttributes(credentialPath, FileAttributes.ReadOnly);
        try
        {
            var first = await provider.GetUsageAsync(CancellationToken.None);

            Assert.AreEqual(ProviderStatus.Ready, first.Status);
            Assert.AreEqual("new-access", handler.LastAuthorizationParameter);
            Assert.IsTrue(provider.HasUnsavedTokens);
            Assert.AreEqual(original, File.ReadAllText(credentialPath));
        }
        finally
        {
            File.SetAttributes(credentialPath, FileAttributes.Normal);
        }

        var second = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, second.Status);
        Assert.AreEqual(3, handler.CallCount, "the spent refresh token must not be sent again");
        Assert.AreEqual("GET https://api.anthropic.com/api/oauth/usage", handler.Requests[2]);
        Assert.AreEqual("new-access", handler.LastAuthorizationParameter);
        Assert.IsFalse(provider.HasUnsavedTokens);
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(credentialPath));
        var oauth = saved.RootElement.GetProperty("claudeAiOauth");
        Assert.AreEqual("new-access", oauth.GetProperty("accessToken").GetString());
        Assert.AreEqual("new-refresh", oauth.GetProperty("refreshToken").GetString());
    }

    [TestMethod]
    public async Task GetUsageAsync_UnsavedRotatedTokensSurviveProviderRebuild()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var account = CreateAccount("claude-account-1", "Claude Work");
        var firstHandler = new QueueHandler(
            CreateTokenResponse("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":28800}"),
            CreateUsageResponse());
        using var firstClient = new HttpClient(firstHandler);
        var first = new ClaudeUsageProvider(firstClient, credentialPath, account);
        File.SetAttributes(credentialPath, FileAttributes.ReadOnly);
        try
        {
            await first.GetUsageAsync(CancellationToken.None);
            Assert.IsTrue(first.HasUnsavedTokens);
        }
        finally
        {
            File.SetAttributes(credentialPath, FileAttributes.Normal);
        }

        // Saving settings rebuilds every provider; the replacement must pick the kept tokens up.
        var secondHandler = new QueueHandler(CreateUsageResponse());
        using var secondClient = new HttpClient(secondHandler);
        var second = new ClaudeUsageProvider(secondClient, credentialPath, account);

        var snapshot = await second.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, snapshot.Status);
        Assert.AreEqual(1, secondHandler.CallCount, "the spent refresh token must not be sent again");
        Assert.AreEqual("new-access", secondHandler.LastAuthorizationParameter);
        Assert.IsFalse(second.HasUnsavedTokens);
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(credentialPath));
        Assert.AreEqual("new-refresh", saved.RootElement.GetProperty("claudeAiOauth").GetProperty("refreshToken").GetString());
    }

    [TestMethod]
    public async Task GetUsageAsync_FreshLoginFileWinsOverUnsavedRotatedTokens()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var handler = new QueueHandler(
            CreateTokenResponse("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":28800}"),
            CreateUsageResponse(),
            CreateUsageResponse());
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));
        File.SetAttributes(credentialPath, FileAttributes.ReadOnly);
        try
        {
            await provider.GetUsageAsync(CancellationToken.None);
            Assert.IsTrue(provider.HasUnsavedTokens);
        }
        finally
        {
            File.SetAttributes(credentialPath, FileAttributes.Normal);
        }

        var freshLogin =
            "{\"claudeAiOauth\":{\"accessToken\":\"login-access\",\"refreshToken\":\"login-refresh\"," +
            $"\"expiresAt\":{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeMilliseconds()}," +
            "\"scopes\":[\"user:profile\"],\"subscriptionType\":\"pro\"}}";
        File.WriteAllText(credentialPath, freshLogin);

        var snapshot = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, snapshot.Status);
        Assert.AreEqual("login-access", handler.LastAuthorizationParameter);
        Assert.AreEqual("pro", snapshot.PlanType);
        Assert.IsFalse(provider.HasUnsavedTokens);
        Assert.AreEqual(freshLogin, File.ReadAllText(credentialPath));
    }

    [TestMethod]
    public async Task GetUsageAsync_SecondRotationWhileFileStaysUnwritableKeepsNewestTokens()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var handler = new QueueHandler(
            // The first rotated token expires at once, forcing a second rotation on the next call.
            CreateTokenResponse("{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":1}"),
            CreateUsageResponse(),
            CreateTokenResponse("{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"expires_in\":28800}"),
            CreateUsageResponse(),
            CreateUsageResponse());
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));
        File.SetAttributes(credentialPath, FileAttributes.ReadOnly);
        try
        {
            await provider.GetUsageAsync(CancellationToken.None);
            await provider.GetUsageAsync(CancellationToken.None);
            StringAssert.Contains(handler.Bodies[2], "\"refresh_token\":\"refresh-1\"");

            var third = await provider.GetUsageAsync(CancellationToken.None);

            Assert.AreEqual(ProviderStatus.Ready, third.Status);
            Assert.AreEqual(5, handler.CallCount, "the spent refresh token must not be sent again");
            Assert.AreEqual("access-2", handler.LastAuthorizationParameter);
            Assert.IsTrue(provider.HasUnsavedTokens);
        }
        finally
        {
            File.SetAttributes(credentialPath, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public async Task GetUsageAsync_LockedCredentialFileIsTransientNotSignedOut()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(2));
        var handler = new QueueHandler(CreateUsageResponse());
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));

        using (new FileStream(credentialPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var locked = await provider.GetUsageAsync(CancellationToken.None);

            Assert.AreEqual(ProviderStatus.UnknownError, locked.Status);
            Assert.AreEqual(0, handler.CallCount);
        }

        var unlocked = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, unlocked.Status);
        Assert.AreEqual("max", unlocked.PlanType);
    }

    [TestMethod]
    public async Task GetUsageAsync_ReplacedFileWithoutRefreshTokenWinsOverUnsavedRotatedTokens()
    {
        var credentialPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var handler = new QueueHandler(
            CreateTokenResponse("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":28800}"),
            CreateUsageResponse(),
            CreateUsageResponse());
        using var client = new HttpClient(handler);
        var provider = new ClaudeUsageProvider(client, credentialPath, CreateAccount("claude-account-1", "Claude Work"));
        File.SetAttributes(credentialPath, FileAttributes.ReadOnly);
        try
        {
            await provider.GetUsageAsync(CancellationToken.None);
        }
        finally
        {
            File.SetAttributes(credentialPath, FileAttributes.Normal);
        }

        var replaced = "{\"claudeAiOauth\":{\"accessToken\":\"other-access\",\"subscriptionType\":\"pro\"}}";
        File.WriteAllText(credentialPath, replaced);

        var snapshot = await provider.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, snapshot.Status);
        Assert.AreEqual("other-access", handler.LastAuthorizationParameter);
        Assert.IsFalse(provider.HasUnsavedTokens);
        Assert.AreEqual(replaced, File.ReadAllText(credentialPath));
    }

    [TestMethod]
    public async Task ForgetPendingTokensExcept_DropsKeptTokensOfRemovedAccounts()
    {
        var keptPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var removedPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddHours(-1));
        var providers = new List<ClaudeUsageProvider>();
        foreach (var path in new[] { keptPath, removedPath })
        {
            var handler = new QueueHandler(
                CreateTokenResponse("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":28800}"),
                CreateUsageResponse());
            var provider = new ClaudeUsageProvider(new HttpClient(handler), path, CreateAccount("claude-account-1", "Claude Work"));
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                await provider.GetUsageAsync(CancellationToken.None);
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Assert.IsTrue(provider.HasUnsavedTokens);
            providers.Add(provider);
        }

        ClaudeUsageProvider.ForgetPendingTokensExcept(new[] { keptPath });

        Assert.IsTrue(providers[0].HasUnsavedTokens);
        Assert.IsFalse(providers[1].HasUnsavedTokens);
        ClaudeUsageProvider.ForgetPendingTokensExcept(Array.Empty<string>());
    }

    [TestMethod]
    public async Task GetUsageAsync_FailedRefreshUsesOldTokenOnlyWhenItIsNotAboutToExpire()
    {
        var nearlyExpiredPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddSeconds(20));
        var nearlyExpiredHandler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var nearlyExpiredClient = new HttpClient(nearlyExpiredHandler);
        var nearlyExpired = new ClaudeUsageProvider(
            nearlyExpiredClient,
            nearlyExpiredPath,
            CreateAccount("claude-account-1", "Claude Work"));

        var blocked = await nearlyExpired.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.UnknownError, blocked.Status);
        Assert.AreEqual(1, nearlyExpiredHandler.CallCount, "a token inside the expiry guard must not be sent");

        var stillValidPath = WriteExpiringCredentials(DateTimeOffset.UtcNow.AddMinutes(3));
        var stillValidHandler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            CreateUsageResponse());
        using var stillValidClient = new HttpClient(stillValidHandler);
        var stillValid = new ClaudeUsageProvider(
            stillValidClient,
            stillValidPath,
            CreateAccount("claude-account-2", "Claude Home"));

        var served = await stillValid.GetUsageAsync(CancellationToken.None);

        Assert.AreEqual(ProviderStatus.Ready, served.Status);
        Assert.AreEqual(2, stillValidHandler.CallCount);
        Assert.AreEqual("old-access", stillValidHandler.LastAuthorizationParameter);
    }

    private string WriteExpiringCredentials(DateTimeOffset expiresAt)
    {
        var path = workspace.GetPath($"{Guid.NewGuid():N}.credentials.json");
        File.WriteAllText(
            path,
            "{\"claudeAiOauth\":{\"accessToken\":\"old-access\",\"refreshToken\":\"old-refresh\"," +
            $"\"expiresAt\":{expiresAt.ToUnixTimeMilliseconds()}," +
            "\"scopes\":[\"user:profile\"],\"subscriptionType\":\"max\"}}");
        return path;
    }

    private static HttpResponseMessage CreateTokenResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private string WriteCredentials(string? subscriptionType = null)
    {
        var property = subscriptionType is null
            ? string.Empty
            : $",\"subscriptionType\":\"{subscriptionType}\"";
        return WriteCredentialsRaw(property);
    }

    private string WriteCredentialsRaw(string subscriptionProperty)
    {
        var path = workspace.GetPath($"{Guid.NewGuid():N}.credentials.json");
        File.WriteAllText(path, $"{{\"claudeAiOauth\":{{\"accessToken\":\"test-token\"{subscriptionProperty}}}}}");
        return path;
    }

    private UsageRefreshCoordinator CreateCoordinator(
        IUsageProvider provider,
        FakeTimeProvider clock,
        string name) => new(
        new[] { provider },
        new UsageCache(workspace.GetPath($"{name}.json")),
        new SafeLogger(workspace.GetPath($"{name}.log")),
        clock);

    private static AgentAccount CreateAccount(string id, string name) => new()
    {
        Id = id,
        Kind = AgentKind.Claude,
        Name = name,
        Enabled = true,
        UseDefaultCredentials = false
    };

    private static HttpResponseMessage CreateUsageResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"five_hour\":{\"utilization\":34,\"resets_at\":\"2026-09-04T15:00:00Z\"}}",
            Encoding.UTF8,
            "application/json")
    };

    private static HttpResponseMessage CreateRateLimitedResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "120");
        return response;
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        public QueueHandler(params HttpResponseMessage[] responses) => this.responses = new Queue<HttpResponseMessage>(responses);

        public int CallCount { get; private set; }

        public string? LastAuthorizationScheme { get; private set; }

        public string? LastAuthorizationParameter { get; private set; }

        public List<string> Requests { get; } = new();

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            Requests.Add($"{request.Method} {request.RequestUri}");
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        }
    }
}
