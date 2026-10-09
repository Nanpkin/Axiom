using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class WindowMonitorTests
{
    private static DesktopMonitor Monitor(int handle, int left, int top, int width, int height, bool primary = false, int taskbar = 40) =>
        new(handle, new(left, top, checked(left + width), checked(top + height)), new(left, top, checked(left + width), checked(top + height - taskbar)), primary);

    [Fact]
    public void DirectionUsesDesktopPositionRatherThanEnumerationOrderAndWraps()
    {
        var left = Monitor(1, -1920, 0, 1920, 1080); var middle = Monitor(2, 0, 0, 2560, 1440, true); var upper = Monitor(3, 0, -1080, 1920, 1080);
        var shuffled = new[] { middle, left, upper };
        Assert.Equal([left, upper, middle], WindowMonitorGeometry.Order(shuffled));
        Assert.Equal(middle, WindowMonitorGeometry.Select(shuffled, left.Handle, "primary"));
        Assert.Equal(upper, WindowMonitorGeometry.Select(shuffled, left.Handle, "next"));
        Assert.Equal(left, WindowMonitorGeometry.Select(shuffled, middle.Handle, "next"));
        Assert.Equal(middle, WindowMonitorGeometry.Select(shuffled, left.Handle, "previous"));
        Assert.Throws<ArgumentException>(() => WindowMonitorGeometry.Select(shuffled, 99, "next"));
    }
    [Fact]
    public void CenterUsesPhysicalWorkAreaWithNegativeOriginsAndOddSizes()
    {
        var negative = Monitor(1, -1921, -1081, 1921, 1081, true, 41);
        Assert.Equal((-1729, -977, 1537, 832), WindowMonitorGeometry.Center(negative));
        var tall = Monitor(2, 2560, -100, 2160, 3840, taskbar: 60);
        Assert.Equal((2776, 278, 1728, 3024), WindowMonitorGeometry.Center(tall));
        // Pixel work areas are used directly: no stored DPI or coordinate scaling guesses.
        var primary = Monitor(3, 0, 0, 3840, 2160, true, 80);
        Assert.Equal((384, 208, 3072, 1664), WindowMonitorGeometry.Center(primary));
    }
    [Fact]
    public void SingleDisplaySelectsItselfAndInvalidOrChangingTopologyIsNeverGuessed()
    {
        var only = Monitor(1, 0, 0, 1920, 1080, true);
        Assert.Same(only, WindowMonitorGeometry.Select([only], 1, "next"));
        Assert.Same(only, WindowMonitorGeometry.Select([only], 1, "previous"));
        Assert.True(WindowMonitorGeometry.SameTopology([only], [only]));
        Assert.False(WindowMonitorGeometry.SameTopology([only], [only with { Work = new(0, 0, 1920, 1000) }]));
        Assert.False(WindowMonitorGeometry.SameTopology([only], [only with { Handle = 2 }]));
        Assert.Throws<ArgumentException>(() => WindowMonitorGeometry.Order([]));
        Assert.Throws<ArgumentException>(() => WindowMonitorGeometry.Order([only, only]));
        Assert.Throws<ArgumentException>(() => WindowMonitorGeometry.Order([only with { Primary = false }]));
        Assert.Throws<ArgumentException>(() => WindowMonitorGeometry.Order([only with { Work = new(0, 0, 1921, 1040) }]));
        Assert.Throws<ArgumentException>(() => WindowMonitorGeometry.Order([only with { Bounds = new(0, 0, 1, 1) }]));
        Assert.Throws<ArgumentException>(() => WindowMonitorGeometry.Select([only], 1, "saved-screen-id"));
    }
    [Theory]
    [InlineData("primary")]
    [InlineData("next")]
    [InlineData("previous")]
    public void MoveCompilesIntoOneShotCommandWithMultiplePathsAndDelay(string direction)
    {
        var descriptor = WindowMonitorCapabilities.Descriptor;
        var invocation = new TaskInvocation(descriptor.Id, descriptor.SchemaVersion, descriptor.Fields.Where(f => f.DefaultValue is not null)
            .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("monitor", direction).SetItem("delaySeconds", "2")
            .Add("executable", Path.Combine(Path.GetTempPath(), "SyntheticEditor.exe") + "\n" + Path.Combine(Path.GetTempPath(), "SyntheticChat.exe")));
        var compiler = new WindowMonitorTaskAction(); var action = compiler.Compile(invocation, ResourceScope.Session("synthetic-owner", 1));
        Assert.Equal(RuleActionKind.Command, action.Kind); Assert.Equal(WindowMonitorCapabilities.Move, action.Key.CapabilityId);
        Assert.Equal(2, WindowMonitorCapabilities.ValidateTargets(invocation).Length);
        Assert.Equal(TimeSpan.FromSeconds(2), compiler.Timing(invocation).MinimumDuration); Assert.True(compiler.SupportsTimeWindow);
        var plugin = new WindowMonitorPlugin();
        Assert.Contains(new TaskCombination(HotkeyCapabilities.Pressed, descriptor.Id), plugin.Combinations);
        Assert.Contains(new TaskCombination(SystemEventCapabilities.Event, descriptor.Id), plugin.Combinations);
        Assert.False(plugin.Bindings.Single().Descriptor.DesiredState); Assert.Equal(RetryCategory.Never, plugin.Bindings.Single().Descriptor.Retry);
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation with { Parameters = invocation.Parameters.SetItem("monitor", "unknown") }, action.Key.Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation with { Parameters = invocation.Parameters.SetItem("waitSeconds", "31") }, action.Key.Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation with { Parameters = invocation.Parameters.Add("deviceId", "x") }, action.Key.Scope));
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation with { Parameters = invocation.Parameters.SetItem("executable", "SyntheticEditor.exe") }, action.Key.Scope));
    }
}
