using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class AgentLoginServiceTests
{
    private TempWorkspace workspace = null!;

    [TestInitialize]
    public void SetUp() => workspace = TempWorkspace.Create();

    [TestCleanup]
    public void TearDown() => workspace.Dispose();

    [TestMethod]
    public async Task LoginAsync_CodexUsesIsolatedEnvironmentAndDoesNotExposeOutput()
    {
        var executablePath = workspace.GetPath("codex.exe");
        File.WriteAllText(executablePath, string.Empty);
        ProcessStartInfo? captured = null;
        var account = AgentAccount.Create(AgentKind.Codex);
        account.Name = "Work";
        var configDirectory = workspace.GetPath("codex-config");
        var service = new AgentLoginService(
            processRunner: (startInfo, _) =>
            {
                captured = startInfo;
                return Task.FromResult(0);
            },
            configDirectoryResolver: _ => configDirectory);

        var result = await service.LoginAsync(account, executablePath, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("로그인이 완료되었습니다.", result.Message);
        Assert.IsNotNull(captured);
        Assert.AreEqual(executablePath, captured.FileName);
        CollectionAssert.AreEqual(
            new[] { "-c", "cli_auth_credentials_store=\"file\"", "login" },
            captured.ArgumentList.ToArray());
        Assert.AreEqual(configDirectory, captured.Environment["CODEX_HOME"]);
        Assert.IsFalse(captured.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.IsFalse(captured.Environment.ContainsKey("CODEX_ACCESS_TOKEN"));
        Assert.IsTrue(captured.RedirectStandardOutput);
        Assert.IsTrue(captured.RedirectStandardError);
        Assert.IsTrue(captured.CreateNoWindow);
    }

    [TestMethod]
    public async Task LoginAsync_FailureCancellationAndLegacyAccountAreExplicit()
    {
        var account = AgentAccount.Create(AgentKind.Codex);
        var missing = new AgentLoginService(configDirectoryResolver: _ => workspace.GetPath("missing-config"));
        var missingResult = await missing.LoginAsync(
            account,
            workspace.GetPath("missing.exe"),
            CancellationToken.None);
        Assert.IsFalse(missingResult.Success);
        StringAssert.Contains(missingResult.Message, "찾을 수 없습니다");

        var executablePath = workspace.GetPath("codex.exe");
        File.WriteAllText(executablePath, string.Empty);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new AgentLoginService(
            processRunner: async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            },
            configDirectoryResolver: _ => workspace.GetPath("cancel-config"));
        using var cancellation = new CancellationTokenSource();
        var loginTask = canceled.LoginAsync(account, executablePath, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        var canceledResult = await loginTask;
        Assert.IsFalse(canceledResult.Success);
        StringAssert.Contains(canceledResult.Message, "취소");

        var legacy = new AgentAccount
        {
            Id = "codex",
            Kind = AgentKind.Codex,
            Name = "Codex",
            UseDefaultCredentials = true
        };
        var legacyResult = await canceled.LoginAsync(legacy, executablePath, CancellationToken.None);
        Assert.IsFalse(legacyResult.Success);
        StringAssert.Contains(legacyResult.Message, "기본 CLI 계정");
    }

    [TestMethod]
    public async Task LoginAsync_NonZeroExitDoesNotIncludeProcessOutput()
    {
        var executablePath = workspace.GetPath("codex.exe");
        File.WriteAllText(executablePath, string.Empty);
        var service = new AgentLoginService(
            processRunner: (_, _) => Task.FromResult(17),
            configDirectoryResolver: _ => workspace.GetPath("failed-config"));

        var result = await service.LoginAsync(
            AgentAccount.Create(AgentKind.Codex),
            executablePath,
            CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("로그인 명령이 실패했습니다. 종료 코드: 17", result.Message);
    }

    [TestMethod]
    public async Task LoginAsync_AllowsOnlyOneLoginProcessAtATime()
    {
        var executablePath = workspace.GetPath("codex.exe");
        File.WriteAllText(executablePath, string.Empty);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var service = new AgentLoginService(
            processRunner: async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return 0;
            },
            configDirectoryResolver: account => workspace.GetPath(account.Id));

        var first = service.LoginAsync(AgentAccount.Create(AgentKind.Codex), executablePath, CancellationToken.None);
        await firstStarted.Task;
        var second = service.LoginAsync(AgentAccount.Create(AgentKind.Codex), executablePath, CancellationToken.None);
        await Task.Yield();
        Assert.AreEqual(1, Volatile.Read(ref callCount));

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.AreEqual(2, callCount);
    }
}
