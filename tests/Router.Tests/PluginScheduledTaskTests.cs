using Router.Host.Plugins;
using Router.Host.Services;

namespace Router.Tests;

[TestClass]
public sealed class PluginScheduledTaskTests
{
    [TestMethod]
    public void CronScheduleUsesChinaStandardTime()
    {
        var now = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

        var next = CronSchedule.GetNext("0 10 10 * * *", now);

        Assert.AreEqual(TimeSpan.FromHours(8), next.Offset);
        Assert.AreEqual(new DateTime(2026, 9, 23, 10, 10, 0), next.DateTime);
    }
}
