using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class SystemEventTests
{
    [Theory]
    [InlineData("locked")]
    [InlineData("unlocked")]
    [InlineData("resumed")]
    [InlineData("ac")]
    [InlineData("battery")]
    [InlineData("network-up")]
    [InlineData("network-down")]
    public void SessionSignalsCompileToEventEdgesAndSaveAsOrdinaryRules(string kind)
    {
        var detection = new TaskInvocation(SystemEventCapabilities.Event, 1, ImmutableDictionary<string,string>.Empty.Add("event", kind));
        var compiled = new SystemEventTaskDetection().Compile(detection, ResourceScope.Session("owner", 1));
        Assert.NotNull(compiled);
        var compiler = new AutomationTaskCompiler(); compiler.Register(new SystemEventTaskDetection()); compiler.Register(new WindowStateTaskAction());
        var descriptor = WindowCapabilities.StateDescriptor;
        var action = new TaskInvocation(descriptor.Id, 1, descriptor.Fields.Where(f => f.DefaultValue is not null).ToImmutableDictionary(f => f.Id, f => f.DefaultValue!)
            .Add("executable", Path.Combine(Path.GetTempPath(), "SyntheticEditor.exe")));
        var task = new AutomationTask(Guid.NewGuid().ToString(), 0, "解锁整理工作窗口", true, detection, action);
        Assert.NotNull(compiler.Compile(task, "owner", ResourceScope.Session("owner", 1)));
        Assert.Contains(new SystemEventPlugin().Combinations, c => c.ActionId == WindowCapabilities.State);
        Assert.DoesNotContain(new SystemEventPlugin().Combinations, c => c.ActionId == HotkeyCapabilities.Send);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("system-unlocked")]
    [InlineData("")]
    public void InvalidSystemEventsAreRejected(string kind)
    {
        Assert.Throws<ArgumentException>(() => SystemEventCapabilities.Validate(new(SystemEventCapabilities.Event, 1, ImmutableDictionary<string,string>.Empty.Add("event", kind))));
    }
    [Fact]
    public void ExplicitSystemOnlyObservationIsPossibleButUnselectedSignalsAreNotRecorded()
    {
        var now = DateTimeOffset.UtcNow;
        var selected = new UsageRecorder(new(5, false, false, [], SystemEvents: true), now);
        Assert.True(selected.Add(new(now, "system-unlocked")));
        Assert.Single(selected.Session.Events); Assert.Empty(selected.Session.Objects);
        var other = new UsageRecorder(new(5, true, false, []), now);
        Assert.False(other.Add(new(now, "system-unlocked")));
        Assert.Throws<ArgumentException>(() => UsageValidation.Options(new(5, false, false, [])));
    }
}
