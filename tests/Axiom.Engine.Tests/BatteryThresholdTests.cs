using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class BatteryThresholdTests
{
    [Fact]
    public void InitialLowBatteryAndDuplicatesDoNotFireAndRisingAboveRearms()
    {
        var events = new BatteryThresholdEvents();
        Assert.Empty(events.Update(9)); Assert.Empty(events.Update(9)); Assert.Empty(events.Update(10));
        Assert.Empty(events.Update(25));
        Assert.Equal(new[] { "battery-low20" }, events.Update(20));
        Assert.Empty(events.Update(20)); Assert.Empty(events.Update(19));
        Assert.Equal(new[] { "battery-low10" }, events.Update(10));
        Assert.Empty(events.Update(0)); Assert.Empty(events.Update(11));
        Assert.Equal(new[] { "battery-low10" }, events.Update(9));
    }
    [Fact]
    public void UnknownBreaksContinuityAndSkippedLevelsEmitEachCrossedThresholdOnce()
    {
        var events = new BatteryThresholdEvents(); Assert.Empty(events.Update(80));
        Assert.Empty(events.Update(255)); Assert.Empty(events.Update(5));
        Assert.Empty(events.Update(uint.MaxValue)); Assert.Empty(events.Update(100));
        Assert.Equal(new[] { "battery-low20", "battery-low10" }, events.Update(8));
        Assert.Empty(events.Update(8)); Assert.Empty(events.Update(101)); Assert.Empty(events.Update(0));
    }
}
