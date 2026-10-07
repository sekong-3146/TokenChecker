using System.Diagnostics;

namespace TokenChecker.Core;

public sealed record AgentLoginResult(bool Success, string Message);

public sealed class AgentLoginService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan timeout;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> processRunner;
    private readonly Func<AgentAccount, string> configDirectoryResolver;
    private readonly SemaphoreSlim loginGate = new(1, 1);

    public AgentLoginService(
        TimeSpan? timeout = null,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? processRunner = null,
        Func<AgentAccount, string>? configDirectoryResolver = null)
    {
        this.timeout = timeout ?? DefaultTimeout;
        if (this.timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        this.processRunner = processRunner ?? RunProcessAsync;
        this.configDirectoryResolver = configDirectoryResolver ?? (account => account.GetConfigDirectory());
    }

    public async Task<AgentLoginResult> LoginAsync(
        AgentAccount account,
        string? codexExecutablePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.UseDefaultCredentials)
        {
            return new AgentLoginResult(false, "기본 CLI 계정은 직접 변경할 수 없습니다. 새 계정을 추가해 로그인하세요.");
        }

        if (!Enum.IsDefined(account.Kind) || !AgentAccount.IsSafeId(account.Id))
        {
            return new AgentLoginResult(false, "계정 설정이 올바르지 않습니다.");
        }

        try
        {
            await loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new AgentLoginResult(false, "로그인이 취소되었습니다.");
        }

        try
        {
            var command = FindCommand(account.Kind, codexExecutablePath);
            if (command is null)
            {
                var toolName = account.Kind == AgentKind.Codex ? "Codex CLI" : "Claude Code";
                return new AgentLoginResult(false, $"{toolName} 실행 파일을 찾을 수 없습니다.");
            }

            var configDirectory = Path.GetFullPath(configDirectoryResolver(account));
            Directory.CreateDirectory(configDirectory);
            var startInfo = CreateStartInfo(command, account.Kind, configDirectory);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);

            try
            {
                var exitCode = await processRunner(startInfo, timeoutCancellation.Token).ConfigureAwait(false);
                return exitCode == 0
                    ? new AgentLoginResult(true, "로그인이 완료되었습니다.")
                    : new AgentLoginResult(false, $"로그인 명령이 실패했습니다. 종료 코드: {exitCode}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new AgentLoginResult(false, "로그인이 취소되었습니다.");
            }
            catch (OperationCanceledException)
            {
                return new AgentLoginResult(false, "로그인 시간이 초과되었습니다.");
            }
            catch (LoginProcessCleanupException)
            {
                return new AgentLoginResult(false, "로그인 프로세스를 안전하게 종료하지 못했습니다.");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new AgentLoginResult(false, $"로그인 명령을 실행할 수 없습니다. 오류 종류: {exception.GetType().Name}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new AgentLoginResult(false, $"계정 로그인 경로를 준비할 수 없습니다. 오류 종류: {exception.GetType().Name}");
        }
        finally
        {
            loginGate.Release();
        }
    }

    private static LoginCommand? FindCommand(AgentKind kind, string? codexExecutablePath)
    {
        if (kind == AgentKind.Codex && !string.IsNullOrWhiteSpace(codexExecutablePath))
        {
            return File.Exists(codexExecutablePath)
                ? new LoginCommand(codexExecutablePath, IsCommandScript(codexExecutablePath))
                : null;
        }

        if (kind == AgentKind.Codex)
        {
            var configured = Environment.GetEnvironmentVariable("TOKEN_CHECKER_CODEX_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return new LoginCommand(configured, IsCommandScript(configured));
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
                Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.exe")
            };
            var installed = candidates.FirstOrDefault(File.Exists);
            if (installed is not null)
            {
                return new LoginCommand(installed, false);
            }
        }
        else
        {
            var installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "bin",
                "claude.exe");
            if (File.Exists(installed))
            {
                return new LoginCommand(installed, false);
            }
        }

        var commandName = kind == AgentKind.Codex ? "codex" : "claude";
        foreach (var path in GetPathEntries())
        {
            var executable = Path.Combine(path, $"{commandName}.exe");
            if (File.Exists(executable))
            {
                return new LoginCommand(executable, false);
            }

            var script = Path.Combine(path, $"{commandName}.cmd");
            if (File.Exists(script))
            {
                return new LoginCommand(script, true);
            }
        }

        return null;
    }

    private static ProcessStartInfo CreateStartInfo(
        LoginCommand command,
        AgentKind kind,
        string configDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.IsCommandScript
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : command.Path,
            WorkingDirectory = configDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (command.IsCommandScript)
        {
            var arguments = kind == AgentKind.Codex
                ? "-c cli_auth_credentials_store=\\\"file\\\" login"
                : "auth login --claudeai";
            startInfo.Arguments = $"/d /s /c \"\"{command.Path}\" {arguments}\"";
        }
        else if (kind == AgentKind.Codex)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            startInfo.ArgumentList.Add("login");
        }
        else
        {
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("login");
            startInfo.ArgumentList.Add("--claudeai");
        }

        startInfo.Environment[kind == AgentKind.Codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"] =
            configDirectory;
        if (kind == AgentKind.Codex)
        {
            startInfo.Environment.Remove("OPENAI_API_KEY");
            startInfo.Environment.Remove("CODEX_ACCESS_TOKEN");
        }

        return startInfo;
    }

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("로그인 명령을 시작하지 못했습니다.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                StopProcess(process);
            }
            catch (Exception exception)
            {
                throw new LoginProcessCleanupException(exception);
            }

            throw;
        }
    }

    private static void StopProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(milliseconds: 1000))
            {
                throw new InvalidOperationException("로그인 프로세스 종료 시간이 초과되었습니다.");
            }
        }
    }

    private static IEnumerable<string> GetPathEntries() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path.Trim().Trim('"'))
        .Where(path => path.Length > 0);

    private static bool IsCommandScript(string path) => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    private sealed record LoginCommand(string Path, bool IsCommandScript);

    private sealed class LoginProcessCleanupException : Exception
    {
        public LoginProcessCleanupException(Exception innerException)
            : base("로그인 프로세스를 종료하지 못했습니다.", innerException)
        {
        }
    }
}
