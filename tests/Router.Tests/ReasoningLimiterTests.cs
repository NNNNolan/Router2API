using FluentAssertions;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ReasoningLimiterTests
{
    [TestMethod]
    public void EffortSelectionPreservesAliasesLowerTiesAndOriginalOrder()
    {
        (string Requested, string[] Supported, string Expected)[] cases =
        [
            (" NORMAL ", ["HIGH", "LOW"], "LOW"),
            ("high", ["future", "xhigh", "medium"], "medium"),
            ("ULTRA", ["minimal", "xhigh"], "xhigh"),
            ("extra-high", ["high", "XHIGH"], "XHIGH"),
            ("future", ["low", "high"], "high"),
            ("Custom", ["custom", "max"], "custom"),
            ("medium", ["normal", "medium"], "normal")
        ];
        foreach (var item in cases)
            ReasoningLimiter.ClosestSupportedLevel(item.Requested, item.Supported).Should().Be(item.Expected);
    }
}
