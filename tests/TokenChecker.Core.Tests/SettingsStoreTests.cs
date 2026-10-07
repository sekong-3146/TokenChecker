using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private TempWorkspace workspace = null!;

    [TestInitialize]
    public void SetUp() => workspace = TempWorkspace.Create();

    [TestCleanup]
    public void TearDown() => workspace.Dispose();

    [TestMethod]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));

        var settings = store.Load();

        Assert.AreEqual(5, settings.RefreshIntervalMinutes);
        Assert.AreEqual(UsageDisplayMode.Used, settings.DisplayMode);
        Assert.AreEqual(75, settings.WarnThresholdPercent);
        Assert.AreEqual(90, settings.CriticalThresholdPercent);
        Assert.IsFalse(settings.StartWithWindows);
        Assert.IsNull(settings.CodexExecutablePath);
        Assert.IsTrue(settings.CodexEnabled);
        Assert.IsTrue(settings.ClaudeEnabled);
        Assert.AreEqual(2, settings.GetAgents().Count);
        Assert.IsTrue(settings.GetAgents().All(account => account.UseDefaultCredentials));
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        File.WriteAllText(store.Path, "{ this is not valid JSON");

        var settings = store.Load();

        Assert.AreEqual(5, settings.RefreshIntervalMinutes);
        Assert.AreEqual(UsageDisplayMode.Used, settings.DisplayMode);
        Assert.AreEqual(75, settings.WarnThresholdPercent);
        Assert.AreEqual(90, settings.CriticalThresholdPercent);
        Assert.IsFalse(settings.StartWithWindows);
        Assert.IsNull(settings.CodexExecutablePath);
        Assert.IsTrue(settings.CodexEnabled);
        Assert.IsTrue(settings.ClaudeEnabled);
    }

    [TestMethod]
    public void Load_NormalizesRefreshIntervals()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        var cases = new[]
        {
            (Input: 2, Expected: 3),
            (Input: 4, Expected: 5),
            (Input: 7, Expected: 10),
            (Input: 99, Expected: 10)
        };

        foreach (var testCase in cases)
        {
            File.WriteAllText(store.Path, $"{{\"RefreshIntervalMinutes\":{testCase.Input}}}");
            Assert.AreEqual(testCase.Expected, store.Load().RefreshIntervalMinutes);
        }
    }

    [TestMethod]
    public void Load_ClampsThresholdsAndCorrectsWarnAboveCritical()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));

        File.WriteAllText(store.Path, "{\"WarnThresholdPercent\":-4,\"CriticalThresholdPercent\":101}");
        var clamped = store.Load();
        Assert.AreEqual(1, clamped.WarnThresholdPercent);
        Assert.AreEqual(100, clamped.CriticalThresholdPercent);

        File.WriteAllText(store.Path, "{\"WarnThresholdPercent\":80,\"CriticalThresholdPercent\":20}");
        var corrected = store.Load();
        Assert.AreEqual(80, corrected.WarnThresholdPercent);
        Assert.AreEqual(80, corrected.CriticalThresholdPercent);
    }

    [TestMethod]
    public void SaveLoad_RoundTripsEveryProperty()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        var expected = new AppSettings
        {
            RefreshIntervalMinutes = 10,
            DisplayMode = UsageDisplayMode.Remaining,
            WarnThresholdPercent = 62,
            CriticalThresholdPercent = 93,
            StartWithWindows = true,
            CodexExecutablePath = @"C:\Tools\codex.exe",
            CodexEnabled = false,
            ClaudeEnabled = false,
            PopupWidth = 440,
            PopupHeight = 310,
            PopupPinned = true,
            PopupScreen = @"\\.\DISPLAY2",
            PopupTransparencyPercent = 35
        };

        store.Save(expected);
        var actual = store.Load();

        Assert.AreEqual(expected.PopupWidth, actual.PopupWidth);
        Assert.AreEqual(expected.PopupHeight, actual.PopupHeight);
        Assert.AreEqual(expected.PopupPinned, actual.PopupPinned);
        Assert.AreEqual(expected.PopupScreen, actual.PopupScreen);
        Assert.AreEqual(expected.PopupTransparencyPercent, actual.PopupTransparencyPercent);

        expected.PopupTransparencyPercent = 100;
        store.Save(expected);
        Assert.AreEqual(AppSettings.MaxPopupTransparencyPercent, store.Load().PopupTransparencyPercent);
        Assert.AreEqual(expected.RefreshIntervalMinutes, actual.RefreshIntervalMinutes);
        Assert.AreEqual(expected.DisplayMode, actual.DisplayMode);
        Assert.AreEqual(expected.WarnThresholdPercent, actual.WarnThresholdPercent);
        Assert.AreEqual(expected.CriticalThresholdPercent, actual.CriticalThresholdPercent);
        Assert.AreEqual(expected.StartWithWindows, actual.StartWithWindows);
        Assert.AreEqual(expected.CodexExecutablePath, actual.CodexExecutablePath);
        Assert.AreEqual(expected.CodexEnabled, actual.CodexEnabled);
        Assert.AreEqual(expected.ClaudeEnabled, actual.ClaudeEnabled);
    }

    [TestMethod]
    public void Save_DoesNotMutateInput_AndCloneIsIndependent()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        var input = new AppSettings
        {
            RefreshIntervalMinutes = 2,
            DisplayMode = UsageDisplayMode.Remaining,
            WarnThresholdPercent = 120,
            CriticalThresholdPercent = -4,
            StartWithWindows = true,
            CodexExecutablePath = "original",
            CodexEnabled = false,
            ClaudeEnabled = false
        };

        store.Save(input);

        Assert.AreEqual(2, input.RefreshIntervalMinutes);
        Assert.AreEqual(UsageDisplayMode.Remaining, input.DisplayMode);
        Assert.AreEqual(120, input.WarnThresholdPercent);
        Assert.AreEqual(-4, input.CriticalThresholdPercent);
        Assert.IsTrue(input.StartWithWindows);
        Assert.AreEqual("original", input.CodexExecutablePath);
        Assert.IsFalse(input.CodexEnabled);
        Assert.IsFalse(input.ClaudeEnabled);

        var clone = input.Clone();
        clone.RefreshIntervalMinutes = 10;
        clone.DisplayMode = UsageDisplayMode.Used;
        clone.WarnThresholdPercent = 1;
        clone.CriticalThresholdPercent = 2;
        clone.StartWithWindows = false;
        clone.CodexExecutablePath = "clone";
        clone.CodexEnabled = true;
        clone.ClaudeEnabled = true;

        Assert.AreEqual(2, input.RefreshIntervalMinutes);
        Assert.AreEqual(UsageDisplayMode.Remaining, input.DisplayMode);
        Assert.AreEqual(120, input.WarnThresholdPercent);
        Assert.AreEqual(-4, input.CriticalThresholdPercent);
        Assert.IsTrue(input.StartWithWindows);
        Assert.AreEqual("original", input.CodexExecutablePath);
        Assert.IsFalse(input.CodexEnabled);
        Assert.IsFalse(input.ClaudeEnabled);
    }

    [TestMethod]
    public void SaveLoad_RoundTripsMixedFourAccountsAndPreservesEmptyList()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        var mixed = new AppSettings
        {
            Agents = new List<AgentAccount>
            {
                CreateAccount("codex-1", AgentKind.Codex, "Codex A"),
                CreateAccount("codex-2", AgentKind.Codex, "Codex B"),
                CreateAccount("codex-3", AgentKind.Codex, "Codex C"),
                CreateAccount("claude-1", AgentKind.Claude, "Claude A")
            }
        };

        store.Save(mixed);
        var loaded = store.Load();

        Assert.AreEqual(4, loaded.GetAgents().Count);
        Assert.AreEqual(3, loaded.GetAgents().Count(account => account.Kind == AgentKind.Codex));
        Assert.AreEqual(1, loaded.GetAgents().Count(account => account.Kind == AgentKind.Claude));

        loaded.Agents = new List<AgentAccount>();
        store.Save(loaded);
        Assert.AreEqual(0, store.Load().GetAgents().Count);
    }

    [TestMethod]
    public void SaveLoad_RoundTripsFourCodexAccounts()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        var settings = new AppSettings
        {
            Agents = Enumerable.Range(1, AppSettings.MaxAgents)
                .Select(index => CreateAccount($"codex-{index}", AgentKind.Codex, $"Codex {index}"))
                .ToList()
        };

        store.Save(settings);

        Assert.AreEqual(4, store.Load().GetAgents().Count);
        Assert.IsTrue(store.Load().GetAgents().All(account => account.Kind == AgentKind.Codex));
    }

    [TestMethod]
    public void Save_RejectsFifthAccountAndDuplicateIds()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        var five = new AppSettings
        {
            Agents = Enumerable.Range(1, AppSettings.MaxAgents + 1)
                .Select(index => CreateAccount($"account-{index}", AgentKind.Codex, $"Account {index}"))
                .ToList()
        };
        Assert.ThrowsException<ArgumentException>(() => store.Save(five));

        var duplicate = new AppSettings
        {
            Agents = new List<AgentAccount>
            {
                CreateAccount("same-id", AgentKind.Codex, "One"),
                CreateAccount("SAME-ID", AgentKind.Claude, "Two")
            }
        };
        Assert.ThrowsException<ArgumentException>(() => store.Save(duplicate));
    }

    [TestMethod]
    public void Load_MigratesLegacyBooleansAndFiltersMalformedAccounts()
    {
        var store = new SettingsStore(workspace.GetPath("settings.json"));
        File.WriteAllText(store.Path, "{\"CodexEnabled\":false,\"ClaudeEnabled\":true}");

        var legacy = store.Load();

        Assert.IsNotNull(legacy.Agents);
        Assert.IsFalse(legacy.GetAgents().Single(account => account.Kind == AgentKind.Codex).Enabled);
        Assert.IsTrue(legacy.GetAgents().Single(account => account.Kind == AgentKind.Claude).Enabled);
        Assert.IsTrue(legacy.GetAgents().All(account => account.UseDefaultCredentials));

        File.WriteAllText(
            store.Path,
            "{\"Agents\":[null,{\"Id\":\"bad-kind\",\"Kind\":99,\"Name\":\"Bad\"},{\"Id\":\"valid\",\"Kind\":\"Codex\",\"Name\":\"  Work  \"}]}");
        var normalized = store.Load();
        Assert.AreEqual(1, normalized.GetAgents().Count);
        Assert.AreEqual("Work", normalized.GetAgents().Single().Name);
    }

    [TestMethod]
    public void Clone_DeepCopiesAccounts()
    {
        var original = new AppSettings
        {
            Agents = new List<AgentAccount> { CreateAccount("codex-1", AgentKind.Codex, "Original") }
        };

        var clone = original.Clone();
        clone.Agents![0].Name = "Changed";
        clone.Agents.Add(CreateAccount("claude-1", AgentKind.Claude, "Added"));

        Assert.AreEqual("Original", original.Agents![0].Name);
        Assert.AreEqual(1, original.Agents.Count);
    }

    [TestMethod]
    public void Save_WhenDestinationIsDirectory_PropagatesFailure()
    {
        var store = new SettingsStore(workspace.DirectoryPath);
        Exception? failure = null;

        try
        {
            store.Save(new AppSettings());
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.IsNotNull(failure);
        Assert.IsTrue(
            failure is IOException or UnauthorizedAccessException,
            $"Unexpected exception type: {failure.GetType().Name}");
    }

    [TestMethod]
    public void AgentAccount_CreateUsesUniqueSafeIdsAndRejectsTraversal()
    {
        var first = AgentAccount.Create(AgentKind.Codex);
        var second = AgentAccount.Create(AgentKind.Codex);

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual(32, first.Id.Length);
        Assert.IsTrue(first.Id.All(char.IsAsciiLetterOrDigit));
        Assert.IsFalse(first.UseDefaultCredentials);

        first.Id = "..\\outside";
        Assert.ThrowsException<InvalidOperationException>(() => first.GetConfigDirectory());
    }

    private static AgentAccount CreateAccount(string id, AgentKind kind, string name) => new()
    {
        Id = id,
        Kind = kind,
        Name = name,
        Enabled = true,
        UseDefaultCredentials = false
    };
}
