using Microsoft.VisualStudio.TestTools.UnitTesting;
using TokenChecker.Core;

namespace TokenChecker.Core.Tests;

[TestClass]
public sealed class BackoffScheduleTests
{
    [TestMethod]
    public void GetDelay_ReturnsFullFailureLadder()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), BackoffSchedule.GetDelay(0));
        Assert.AreEqual(TimeSpan.FromMinutes(1), BackoffSchedule.GetDelay(1));
        Assert.AreEqual(TimeSpan.FromMinutes(2), BackoffSchedule.GetDelay(2));
        Assert.AreEqual(TimeSpan.FromMinutes(5), BackoffSchedule.GetDelay(3));
        Assert.AreEqual(TimeSpan.FromMinutes(10), BackoffSchedule.GetDelay(4));
        Assert.AreEqual(TimeSpan.FromMinutes(15), BackoffSchedule.GetDelay(5));
        Assert.AreEqual(TimeSpan.FromMinutes(15), BackoffSchedule.GetDelay(50));
    }
}
