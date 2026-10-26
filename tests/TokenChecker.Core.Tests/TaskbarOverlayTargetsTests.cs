using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class TaskbarOverlayTargetsTests
{
    private static readonly DisplayScreen[] Screens =
    {
        new(@"\\.\DISPLAY17", Primary: false),
        new(@"\\.\DISPLAY18", Primary: true)
    };

    [TestMethod]
    public void Resolve_DefaultsToThePrimaryMonitor()
    {
        CollectionAssert.AreEqual(new[] { @"\\.\DISPLAY18" }, TaskbarOverlayTargets.Resolve(null, Screens).ToArray());
        CollectionAssert.AreEqual(new[] { @"\\.\DISPLAY18" }, TaskbarOverlayTargets.Resolve("  ", Screens).ToArray());
    }

    [TestMethod]
    public void Resolve_AllListsEveryMonitorPrimaryFirst()
    {
        CollectionAssert.AreEqual(
            new[] { @"\\.\DISPLAY18", @"\\.\DISPLAY17" },
            TaskbarOverlayTargets.Resolve(AppSettings.AllTaskbarOverlayScreens, Screens).ToArray());
    }

    [TestMethod]
    public void Resolve_ChosenMonitorFallsBackToPrimaryWhileDisconnected()
    {
        CollectionAssert.AreEqual(
            new[] { @"\\.\DISPLAY17" },
            TaskbarOverlayTargets.Resolve(@"\\.\display17", Screens).ToArray());
        CollectionAssert.AreEqual(
            new[] { @"\\.\DISPLAY18" },
            TaskbarOverlayTargets.Resolve(@"\\.\DISPLAY3", Screens).ToArray());
        Assert.AreEqual(0, TaskbarOverlayTargets.Resolve(null, Array.Empty<DisplayScreen>()).Count);
    }
}
