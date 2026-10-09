using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class WindowStateTests
{
    [Theory]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("restore")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("center")]
    [InlineData("topmost")]
    [InlineData("notopmost")]
    public void StateActionCompilesWithDelayAndMultipleTargets(string state)
    {
        var action = new TaskInvocation(WindowCapabilities.State, 1, ImmutableDictionary<string, string>.Empty
            .Add("executable", Path.Combine(Path.GetTempPath(), "Editor.exe") + "\n" + Path.Combine(Path.GetTempPath(), "Chat.exe")).Add("state", state).Add("waitSeconds", "30").Add("delaySeconds", "2"));
        var compiler = new WindowStateTaskAction();
        Assert.Equal(2, WindowCapabilities.ValidateTargets(action).Length);
        var rule = compiler.Compile(action, ResourceScope.Session("owner", 1));
        Assert.Equal(RuleActionKind.Command, rule.Kind);
        Assert.Equal(WindowCapabilities.State, rule.Key.CapabilityId);
        Assert.Equal(TimeSpan.FromSeconds(2), compiler.Timing(action).MinimumDuration);
        Assert.Contains(new WindowPlugin().Combinations, c => c.DetectionId == HotkeyCapabilities.Pressed && c.ActionId == WindowCapabilities.State);
    }

    [Fact]
    public void LayoutUsesWorkAreaIncludingNegativeMonitorOriginAndOddWidths()
    {
        Assert.Equal((-1920, 40, 960, 1040), Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("left", -1920, 40, 1921, 1040));
        Assert.Equal((-960, 40, 961, 1040), Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("right", -1920, 40, 1921, 1040));
        Assert.Equal((192, 104, 1536, 832), Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("center", 0, 0, 1920, 1040));
        Assert.Throws<ArgumentException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("left", 0, 0, 0, 10));
    }
    [Fact]
    public void LayoutCategoriesAllowFrameMarginsButDoNotInventAMatchForArbitraryPlacement()
    {
        Assert.Equal("left", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(-1928, 32, 976, 1056, -1920, 40, 1921, 1040, 16));
        Assert.Equal("right", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(-960, 40, 961, 1040, -1920, 40, 1921, 1040, 16));
        Assert.Equal("center", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(192, 104, 1536, 832, 0, 0, 1920, 1040, 16));
        Assert.Equal("normal", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(100, 100, 500, 300, 0, 0, 1920, 1040, 16));
        Assert.Throws<ArgumentException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Classify(0, 0, 0, 20, 0, 0, 1920, 1040, 16));
    }
    [Theory]
    [InlineData("state", "unsupported-state")]
    [InlineData("waitSeconds", "31")]
    [InlineData("executable", "cmd.exe")]
    [InlineData("execute", "anything")]
    public void InvalidStateOrExtraParametersAreRejected(string key, string value)
    {
        var action = new TaskInvocation(WindowCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("executable", Path.Combine(Path.GetTempPath(), "Editor.exe")).SetItem(key, value));
        Assert.Throws<ArgumentException>(() => new WindowStateTaskAction().Compile(action, ResourceScope.Session("owner", 1)));
    }
}
