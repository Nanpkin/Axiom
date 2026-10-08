using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Engine;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class WindowPresenceTests
{
    [Fact]
    public void LastVisibleWindowControlsPresenceAndUnknownDoesNotImplyAbsence()
    {
        var state = new WindowPresenceState(["Editor.exe", "Browser.exe"]);
        state.Set(1, true, "Editor.exe", true); state.Set(2, true, "editor.exe", true);
        Assert.True(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(1, false, null, true);
        Assert.True(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(2, false, null, true);
        Assert.False(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(3, true, "Editor.exe", false);
        Assert.Null(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        Assert.False(state.Samples().Single(s => s.ProcessName == "Browser.exe").Visible);
        state.Set(4, true, "Editor.exe", true); Assert.True(state.Samples().Single(s => s.ProcessName == "Editor.exe").Visible);
        state.Set(3, false, null, true); state.Set(4, false, null, true); state.Set(5, true, null, false);
        Assert.All(state.Samples(), s => Assert.Null(s.Visible));
    }
    [Fact]
    public void IdentityChangesReplaceCachedWindowAndPositiveSamplesPrecedeNegativeOnes()
    {
        var state = new WindowPresenceState(["A.exe", "B.exe"]);
        state.Set(1, true, "A.exe", true); state.Set(1, true, "B.exe", true);
        var samples = state.Samples(); Assert.Equal("B.exe", samples[0].ProcessName); Assert.True(samples[0].Visible); Assert.False(samples[1].Visible);
        state.Set(1, true, "Unrelated.exe", true); Assert.All(state.Samples(), s => Assert.False(s.Visible));
    }
    [Fact]
    public void WindowTrackingFailsExplicitlyAtCapacityAndRemovalFreesCapacity()
    {
        var state = new WindowPresenceState(["A.exe"]);
        for (var i = 1; i <= 4096; i++) state.Set(i, true, "A.exe", true);
        Assert.Throws<IOException>(() => state.Set(4097, true, "A.exe", true));
        state.Set(1, false, null, true); state.Set(4097, true, "A.exe", true); Assert.True(state.Samples().Single().Visible);
    }
    [Fact]
    public void WindowDetectionCompilesWithWindowActionAndRejectsPathAsProcessName()
    {
        var compiler = new WindowPresenceTaskDetection();
        var invocation = new TaskInvocation(WindowPresenceCapabilities.Visible, 1, ImmutableDictionary<string,string>.Empty.Add("processNames", "A.exe\nB.exe"));
        Assert.Equal(2, compiler.Compile(invocation, ResourceScope.Session("owner", 1)).Dependencies.Count);
        Assert.Contains(new WindowPresencePlugin().Combinations, c => c.ActionId == WindowCapabilities.State);
        Assert.Throws<ArgumentException>(() => compiler.Compile(invocation with { Parameters = invocation.Parameters.SetItem("processNames", "C:\\Apps\\A.exe") }, ResourceScope.Session("owner", 1)));
    }
    [Fact]
    public void AbsenceRequiresEverySelectedApplicationToBeKnownAbsentAndSharesPresenceResources()
    {
        var scope = ResourceScope.Session("owner", 1);
        var invocation = new TaskInvocation(WindowPresenceCapabilities.Absent, 1, ImmutableDictionary<string, string>.Empty.Add("processNames", "A.exe\nB.exe"));
        var condition = new WindowAbsenceTaskDetection().Compile(invocation, scope);
        Assert.Equal(ConditionKind.All, condition.Kind);
        Assert.All(condition.Dependencies, key => Assert.Equal(WindowPresenceCapabilities.Visible, key.CapabilityId));
        var source = new SourceSession("synthetic-window-source", 1);
        var first = new ResourceKey(WindowPresenceCapabilities.Visible, "a.exe", scope);
        var second = new ResourceKey(WindowPresenceCapabilities.Visible, "b.exe", scope);
        ObservedState State(ResourceKey key, bool? visible) => new(new(key, visible.HasValue ? StateQuality.Known : StateQuality.Unknown,
            visible.HasValue ? StateValue.From(new("axiom.bool", 1), visible.Value) : null, DateTimeOffset.UtcNow), source, 1, 1);
        TruthValue Evaluate(bool? a, bool? b) => RuleEvaluator.Evaluate(condition,
            new Dictionary<ResourceKey, ObservedState> { [first] = State(first, a), [second] = State(second, b) }).Value;
        Assert.Equal(TruthValue.False, Evaluate(true, false)); Assert.Equal(TruthValue.False, Evaluate(false, true));
        Assert.Equal(TruthValue.True, Evaluate(false, false)); Assert.Equal(TruthValue.Unknown, Evaluate(false, null));
        Assert.Equal(TruthValue.Unknown, Evaluate(null, null));
        Assert.Contains(new WindowPresencePlugin().Combinations, c => c.DetectionId == WindowPresenceCapabilities.Absent && c.ActionId == "program.close");
        Assert.DoesNotContain(new WindowPresencePlugin().Combinations, c => c.ActionId == HotkeyCapabilities.Send);
    }

    [Fact(Timeout = 10000)]
    public async Task AbsenceCommandsWaitForLastWindowAndNeverReplayInitialOrResumedAbsence()
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(WindowPresenceCapabilities.Visible, schema: schema));
        var condition = new WindowAbsenceTaskDetection().Compile(new(WindowPresenceCapabilities.Absent, 1,
            ImmutableDictionary<string, string>.Empty.Add("processNames", "A.exe\nB.exe")), ResourceScope.Machine);
        var keys = condition.Dependencies.OrderBy(k => k.ResourceId).ToArray();
        var source = fixture.Engine.RegisterSource("synthetic-windows", keys); long sequence = 0;
        Observation Sample(int index, bool visible) => new(keys[index], StateQuality.Known, StateValue.From(schema, visible), fixture.Clock.UtcNow);
        void Set(int index, bool visible) => fixture.Engine.Update(source, ++sequence, Sample(index, visible));
        var rule = new RuleDefinition("window-cleanup", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        fixture.Engine.ApplySnapshot(source, sequence, [Sample(0, false), Sample(1, false)]);
        fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync();
        Assert.Equal(0, executor.Count);
        Set(0, true); Set(1, true); Set(0, false); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(1, false); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.ApplySnapshot(source, ++sequence, [Sample(0, false), Sample(1, false)]);
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new("window-cleanup", 2, "owner", ResourceScope.Machine, condition, rule.Action, enabled: false));
        Set(0, true); Set(0, false); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new("window-cleanup", 3, "owner", ResourceScope.Machine, condition, rule.Action));
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(0, true); Set(0, false); (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }

}
