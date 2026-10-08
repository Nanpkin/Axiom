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

    [Theory]
    [InlineData("state", "topmost")]
    [InlineData("waitSeconds", "31")]
    [InlineData("executable", "cmd.exe")]
    [InlineData("execute", "anything")]
    public void InvalidStateOrExtraParametersAreRejected(string key, string value)
    {
        var action = new TaskInvocation(WindowCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("executable", Path.Combine(Path.GetTempPath(), "Editor.exe")).SetItem(key, value));
        Assert.Throws<ArgumentException>(() => new WindowStateTaskAction().Compile(action, ResourceScope.Session("owner", 1)));
    }
}
