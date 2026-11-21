using Xunit;

namespace BatteryTray.Tests;

public class ClickGateTests
{
    static readonly DateTime T0 = new(2026, 11, 21, 12, 0, 0);

    [Fact]
    public void SingleClickIsAllowedWithoutPriorDoubleClick() => Assert.True(new ClickGate().AllowSingle(T0));

    [Fact]
    public void ReleaseRightAfterDoubleClickIsIgnored()
    {
        var g = new ClickGate();
        g.MarkDouble(T0);
        Assert.False(g.AllowSingle(T0.AddMilliseconds(30)));
        Assert.False(g.AllowSingle(T0.AddMilliseconds(ClickGate.SuppressMs - 1)));
    }

    [Fact]
    public void LaterSingleClickWorksAgain()
    {
        var g = new ClickGate();
        g.MarkDouble(T0);
        Assert.True(g.AllowSingle(T0.AddMilliseconds(ClickGate.SuppressMs + 1)));
    }

    [Fact]
    public void ClockGoingBackwardNeverBlocksForever()
    {
        var g = new ClickGate();
        g.MarkDouble(T0);
        Assert.True(g.AllowSingle(T0.AddHours(1)));
    }
}
