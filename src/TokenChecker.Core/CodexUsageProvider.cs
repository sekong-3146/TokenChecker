using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TokenChecker.Core;

public sealed class CodexUsageProvider : IUsageProvider, IPushUsageProvider, IDisposable, IAsyncDisposable
{
    private const string RateLimitsUpdatedMethod = "account/rateLimits/updated";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan startupTimeout;
    private readonly TimeSpan requestTimeout;
    private readonly string? executablePath;
    private readonly AgentAccount? account;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object sync = new();
    private long nextRequestId;
    private AppServerSession? session;
    private string? planType;
    private bool restartAttempted;
    private bool disposed;

    public CodexUsageProvider(
        string? executablePath = null,
        TimeSpan? timeout = null,
        AgentAccount? account = null)
    {
        if (account is not null && account.Kind != AgentKind.Codex)
        {
            throw new ArgumentException("Codex 공급자에는 Codex 계정만 사용할 수 있습니다.", nameof(account));
        }

        this.executablePath = executablePath;
        this.account = account?.Clone();
        startupTimeout = timeout ?? DefaultStartupTimeout;
        requestTimeout = timeout ?? DefaultRequestTimeout;
    }

    public string Id => account?.Id ?? "codex";

    public string? DisplayName => account?.Name ?? "Codex";

    public string? ProviderKind => "codex";

    public string? PlanType
    {
        get
        {
            lock (sync)
            {
                return planType;
            }
        }
    }

    public event EventHandler<UsageSnapshot>? UsageUpdated;

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var retrievedAt = DateTimeOffset.UtcNow;
        var command = FindCommand();
        if (command is null)
        {
            return CreateSnapshot(ProviderStatus.NotInstalled, retrievedAt);
        }

        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var snapshot = await GetUsageWithRestartAsync(command, retrievedAt, cancellationToken).ConfigureAwait(false);
            if (snapshot.Status == ProviderStatus.Ready)
            {
                restartAttempted = false;
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            StopCurrentSession();
            return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
        }
        catch (TimeoutException)
        {
            StopCurrentSession();
            return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
        }
        catch (JsonRpcException exception)
        {
            return CreateSnapshot(ClassifyError(exception.ErrorMessage), retrievedAt);
        }
        catch (JsonException)
        {
            return CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt);
        }
        catch (MalformedResponseException)
        {
            return CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt);
        }
        catch (AppServerExitedException)
        {
            StopCurrentSession();
            return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
        }
        catch (ObjectDisposedException)
        {
            StopCurrentSession();
            return CreateSnapshot(ProviderStatus.UnknownError, retrievedAt);
        }
        catch (InvalidOperationException)
        {
            StopCurrentSession();
            return CreateSnapshot(ProviderStatus.UnknownError, retrievedAt);
        }
        catch (IOException)
        {
            StopCurrentSession();
            return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
        }
        catch (ArgumentException)
        {
            return CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt);
        }
        catch (Exception)
        {
            StopCurrentSession();
            return CreateSnapshot(ProviderStatus.UnknownError, retrievedAt);
        }
        finally
        {
            requestGate.Release();
        }
    }

    public void Dispose()
    {
        AppServerSession? activeSession;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeSession = session;
            session = null;
            UsageUpdated = null;
        }

        activeSession?.Stop();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // The app-server rejects any stdin byte that is not JSON, so the writer must stay BOM-free.
    private static readonly Encoding Jsonl = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private async Task<UsageSnapshot> GetUsageWithRestartAsync(
        CodexCommand command,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetUsageCoreAsync(command, retrievedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (AppServerExitedException) when (!cancellationToken.IsCancellationRequested)
        {
            if (restartAttempted)
            {
                StopCurrentSession();
                return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
            }

            restartAttempted = true;
            StopCurrentSession();
            try
            {
                return await GetUsageCoreAsync(command, retrievedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonRpcException exception)
            {
                return CreateSnapshot(ClassifyError(exception.ErrorMessage), retrievedAt);
            }
            catch (JsonException)
            {
                return CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt);
            }
            catch (MalformedResponseException)
            {
                return CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt);
            }
            catch (TimeoutException)
            {
                StopCurrentSession();
                return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
            }
            catch (OperationCanceledException)
            {
                StopCurrentSession();
                return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
            }
            catch (Exception)
            {
                StopCurrentSession();
                return CreateSnapshot(ProviderStatus.NetworkError, retrievedAt);
            }
        }
    }

    private async Task<UsageSnapshot> GetUsageCoreAsync(
        CodexCommand command,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
    {
        var activeSession = await EnsureSessionAsync(command, cancellationToken).ConfigureAwait(false);
        EnsureSessionAlive(activeSession);

        var requestId = NextRequestId();
        var response = await SendRequestAsync(
            activeSession,
            requestId,
            new { method = "account/rateLimits/read", id = requestId },
            requestTimeout,
            cancellationToken).ConfigureAwait(false);

        if (TryGetError(response, out var errorMessage))
        {
            return CreateSnapshot(ClassifyError(errorMessage), retrievedAt);
        }

        if (!TryGetResult(response, out var result))
        {
            return CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt);
        }

        SetPlanType(CodexUsageMapper.ParsePlanType(result), preserveWhenMissing: false);
        var windows = CodexUsageMapper.Parse(result);
        return windows.Count == 0
            ? CreateSnapshot(ProviderStatus.InvalidResponse, retrievedAt)
            : CreateSnapshot(ProviderStatus.Ready, retrievedAt, windows);
    }

    private async Task<AppServerSession> EnsureSessionAsync(CodexCommand command, CancellationToken cancellationToken)
    {
        AppServerSession? activeSession;
        lock (sync)
        {
            ThrowIfDisposed();
            activeSession = session;
        }

        if (activeSession is not null)
        {
            if (activeSession.IsAlive)
            {
                return activeSession;
            }

            StopSession(activeSession);
            throw new AppServerExitedException();
        }

        return await StartSessionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppServerSession> StartSessionAsync(CodexCommand command, CancellationToken cancellationToken)
    {
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(startupTimeout);
        var startupToken = startupCancellation.Token;

        Process? process = null;
        AppServerSession? startedSession = null;
        try
        {
            process = StartAppServer(command);
            startedSession = new AppServerSession(process);
            lock (sync)
            {
                ThrowIfDisposed();
                session = startedSession;
            }

            startedSession.StartReader(ReaderLoopAsync);
            var initializeId = NextRequestId();
            var initializeResponse = await SendRequestAsync(
                startedSession,
                initializeId,
                new
                {
                    method = "initialize",
                    id = initializeId,
                    @params = new
                    {
                        clientInfo = new { name = "token_checker", title = "TokenChecker", version = "1.0.2" }
                    }
                },
                startupTimeout,
                startupToken).ConfigureAwait(false);

            if (TryGetError(initializeResponse, out var errorMessage))
            {
                throw new JsonRpcException(errorMessage);
            }

            try
            {
                await WriteAsync(
                    startedSession.Process,
                    new { method = "initialized", @params = new { } },
                    startupToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                throw new AppServerExitedException(exception);
            }

            return startedSession;
        }
        catch
        {
            if (startedSession is not null)
            {
                StopSession(startedSession);
            }
            else if (process is not null)
            {
                StopProcess(process);
            }

            throw;
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        AppServerSession activeSession,
        long requestId,
        object message,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        EnsureSessionAlive(activeSession);
        var pending = new PendingResponse();
        if (!activeSession.Pending.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException();
        }

        try
        {
            try
            {
                await WriteAsync(activeSession.Process, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                throw new AppServerExitedException(exception);
            }

            return await pending.Completion.Task.WaitAsync(requestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            activeSession.Pending.TryRemove(requestId, out _);
            pending.Abandon();
        }
    }

    private async Task ReaderLoopAsync(AppServerSession activeSession)
    {
        try
        {
            while (!activeSession.ReaderCancellation.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await activeSession.Process.StandardOutput.ReadLineAsync(activeSession.ReaderCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (activeSession.ReaderCancellation.IsCancellationRequested)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    ProcessLine(activeSession, line);
                }
                catch (JsonException)
                {
                    activeSession.FailPending(new MalformedResponseException());
                }
                catch (Exception)
                {
                    // A malformed notification or subscriber must never stop response routing.
                }
            }
        }
        catch (OperationCanceledException) when (activeSession.ReaderCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!activeSession.ReaderCancellation.IsCancellationRequested)
            {
                activeSession.FailPending(new AppServerExitedException());
            }
        }
        finally
        {
            activeSession.MarkReaderCompleted();
            if (!activeSession.ReaderCancellation.IsCancellationRequested)
            {
                activeSession.FailPending(new AppServerExitedException());
            }
        }
    }

    private void ProcessLine(AppServerSession activeSession, string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            if (string.Equals(method.GetString(), RateLimitsUpdatedMethod, StringComparison.Ordinal))
            {
                RaiseUsageUpdated(root);
            }

            return;
        }

        if (!TryGetRequestId(root, out var responseId) ||
            !activeSession.Pending.TryGetValue(responseId, out var pending))
        {
            return;
        }

        pending.TryComplete(root.Clone());
    }

    private void RaiseUsageUpdated(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        SetPlanType(CodexUsageMapper.ParsePlanType(parameters), preserveWhenMissing: true);
        var windows = CodexUsageMapper.Parse(parameters);
        if (windows.Count == 0)
        {
            return;
        }

        if (UsageDisplayFormatter.IsCodexProPlan(PlanType) &&
            !windows.Any(window => window.Scope == UsageScope.Overall))
        {
            return;
        }

        var snapshot = CreateSnapshot(ProviderStatus.Ready, DateTimeOffset.UtcNow, windows);
        var handlers = UsageUpdated;
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
                // Provider observers are isolated from the app-server reader.
            }
        }
    }

    private void StopCurrentSession()
    {
        AppServerSession? activeSession;
        lock (sync)
        {
            activeSession = session;
            session = null;
        }

        activeSession?.Stop();
    }

    private void StopSession(AppServerSession activeSession)
    {
        lock (sync)
        {
            if (ReferenceEquals(session, activeSession))
            {
                session = null;
            }
        }

        activeSession.Stop();
    }

    private static void EnsureSessionAlive(AppServerSession activeSession)
    {
        if (!activeSession.IsAlive)
        {
            throw new AppServerExitedException();
        }
    }

    private long NextRequestId() => Interlocked.Increment(ref nextRequestId);

    private void ThrowIfDisposed()
    {
        lock (sync)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(CodexUsageProvider));
            }
        }
    }

    private static bool TryGetError(JsonElement root, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        errorMessage = error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? string.Empty
            : string.Empty;
        return true;
    }

    private static ProviderStatus ClassifyError(string errorMessage) =>
        errorMessage.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        errorMessage.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
        errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        errorMessage.Contains("credential", StringComparison.OrdinalIgnoreCase)
            ? ProviderStatus.SignedOut
            : ProviderStatus.InvalidResponse;

    private CodexCommand? FindCommand()
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return File.Exists(executablePath)
                ? new CodexCommand(executablePath, IsCommandScript(executablePath))
                : null;
        }

        var configured = Environment.GetEnvironmentVariable("TOKEN_CHECKER_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return new CodexCommand(configured, IsCommandScript(configured));
        }

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(installed))
        {
            return new CodexCommand(installed, false);
        }

        var localInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin", "codex.exe");
        if (File.Exists(localInstall))
        {
            return new CodexCommand(localInstall, false);
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .ToArray();
        var executable = pathEntries.Select(path => Path.Combine(path, "codex.exe")).FirstOrDefault(File.Exists);
        if (executable is not null)
        {
            return new CodexCommand(executable, false);
        }

        var script = pathEntries.Select(path => Path.Combine(path, "codex.cmd")).FirstOrDefault(File.Exists);
        return script is null ? null : new CodexCommand(script, true);
    }

    private static bool IsCommandScript(string path) => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    private Process StartAppServer(CodexCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.IsCommandScript ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : command.Path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Jsonl,
            StandardOutputEncoding = Jsonl,
            StandardErrorEncoding = Jsonl
        };
        if (command.IsCommandScript)
        {
            var isolatedOption = account is { UseDefaultCredentials: false }
                ? "-c cli_auth_credentials_store=\\\"file\\\" "
                : string.Empty;
            startInfo.Arguments = $"/d /s /c \"\"{command.Path}\" {isolatedOption}app-server\"";
        }
        else
        {
            if (account is { UseDefaultCredentials: false })
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            }

            startInfo.ArgumentList.Add("app-server");
        }

        if (account is not null)
        {
            var configDirectory = account.GetConfigDirectory();
            Directory.CreateDirectory(configDirectory);
            startInfo.Environment["CODEX_HOME"] = configDirectory;
            if (!account.UseDefaultCredentials)
            {
                startInfo.Environment.Remove("OPENAI_API_KEY");
                startInfo.Environment.Remove("CODEX_ACCESS_TOKEN");
            }
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException();
    }

    private static async Task WriteAsync(Process process, object message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetRequestId(JsonElement root, out long requestId)
    {
        requestId = 0;
        return root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number &&
               id.TryGetInt64(out requestId);
    }

    private static bool TryGetResult(JsonElement root, out JsonElement result)
    {
        result = default;
        return root.TryGetProperty("result", out result);
    }

    private UsageSnapshot CreateSnapshot(
        ProviderStatus status,
        DateTimeOffset retrievedAt,
        IReadOnlyList<UsageWindow>? windows = null) =>
        new(
            Id,
            status,
            windows ?? Array.Empty<UsageWindow>(),
            retrievedAt,
            DisplayName: DisplayName,
            ProviderKind: ProviderKind,
            PlanType: PlanType);

    private void SetPlanType(string? value, bool preserveWhenMissing)
    {
        if (value is null && preserveWhenMissing)
        {
            return;
        }

        lock (sync)
        {
            planType = value;
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception)
            {
            }

            try
            {
                if (!process.HasExited)
                {
                    process.WaitForExit(milliseconds: 250);
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(milliseconds: 250);
                }
            }
            catch (Exception)
            {
                // A dead or unreachable child process needs no further teardown.
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class AppServerSession
    {
        private int stopped;
        private int readerCompleted;

        public AppServerSession(Process process)
        {
            Process = process;
            ReaderCancellation = new CancellationTokenSource();
            _ = DrainStandardErrorAsync(process);
        }

        public Process Process { get; }

        public CancellationTokenSource ReaderCancellation { get; }

        public ConcurrentDictionary<long, PendingResponse> Pending { get; } = new();

        public Task ReaderTask { get; private set; } = Task.CompletedTask;

        public bool IsAlive
        {
            get
            {
                if (Volatile.Read(ref stopped) != 0 || Volatile.Read(ref readerCompleted) != 0 || ReaderTask.IsCompleted)
                {
                    return false;
                }

                try
                {
                    return !Process.HasExited;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public void StartReader(Func<AppServerSession, Task> reader)
        {
            ReaderTask = Task.Run(() => reader(this));
        }

        public void MarkReaderCompleted() => Volatile.Write(ref readerCompleted, 1);

        public void FailPending(Exception exception)
        {
            foreach (var pending in Pending.Values)
            {
                pending.TryFail(exception);
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
            {
                return;
            }

            try
            {
                ReaderCancellation.Cancel();
            }
            catch (Exception)
            {
            }

            FailPending(new AppServerExitedException());
            StopProcess(Process);
            try
            {
                ReaderTask.Wait(millisecondsTimeout: 500);
            }
            catch (Exception)
            {
            }

            ReaderCancellation.Dispose();
        }

        private static async Task DrainStandardErrorAsync(Process process)
        {
            try
            {
                await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class PendingResponse
    {
        private readonly object sync = new();
        private bool abandoned;

        public TaskCompletionSource<JsonElement> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void TryComplete(JsonElement response)
        {
            lock (sync)
            {
                if (abandoned || !Completion.TrySetResult(response))
                {
                    return;
                }
            }
        }

        public void TryFail(Exception exception)
        {
            lock (sync)
            {
                if (!abandoned)
                {
                    Completion.TrySetException(exception);
                }
            }
        }

        public void Abandon()
        {
            lock (sync)
            {
                abandoned = true;
            }
        }
    }

    private sealed class AppServerExitedException : Exception
    {
        public AppServerExitedException(Exception? innerException = null)
            : base("The Codex app-server process exited.", innerException)
        {
        }
    }

    private sealed class JsonRpcException : Exception
    {
        public JsonRpcException(string errorMessage)
            : base("The Codex app-server returned an error.")
        {
            ErrorMessage = errorMessage;
        }

        public string ErrorMessage { get; }
    }

    private sealed class MalformedResponseException : Exception
    {
    }

    private sealed record CodexCommand(string Path, bool IsCommandScript);
}
