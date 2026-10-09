using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.WinUI;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class TimeWindowTests
{
    [Fact]
    public void WeeklyBoundariesOvernightAndCurrentTimezoneAreExplicit()
    {
        var monday = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var day = new WeeklyTimeWindow(540, 1080, 2);
        Assert.False(day.Contains(monday.AddHours(9).AddTicks(-1), TimeZoneInfo.Utc));
        Assert.True(day.Contains(monday.AddHours(9), TimeZoneInfo.Utc));
        Assert.False(day.Contains(monday.AddHours(18), TimeZoneInfo.Utc));
        Assert.False(day.Contains(monday.AddDays(1).AddHours(10), TimeZoneInfo.Utc));
        var night = new WeeklyTimeWindow(1320, 120, 2);
        Assert.True(night.Contains(monday.AddHours(23), TimeZoneInfo.Utc));
        Assert.True(night.Contains(monday.AddDays(1).AddHours(1), TimeZoneInfo.Utc));
        Assert.False(night.Contains(monday.AddDays(1).AddHours(2), TimeZoneInfo.Utc));
        Assert.False(night.Contains(monday.AddHours(1), TimeZoneInfo.Utc));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Synthetic +08", TimeSpan.FromHours(8), "Synthetic", "Synthetic");
        Assert.True(day.Contains(monday.AddHours(1), zone));
        Assert.False(day.Contains(monday.AddHours(10), zone));
        foreach (var invalid in new[] { new WeeklyTimeWindow(-1, 60, 127), new(60, 1440, 127), new(60, 60, 127), new(0, 60, 0), new(0, 60, 128) })
            Assert.Throws<ArgumentException>(invalid.Validate);
    }

    [Fact(Timeout = 10000)]
    public async Task OutsideEdgeIsDiscardedWithoutOpeningTimerOrReplay()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule(command: true, timing: new(timeWindow: new(60, 120, 127)))); f.Engine.StartExecution();
        f.Update(true); await f.Engine.WaitForIdleAsync();
        Assert.Contains("OutsideTimeWindow", f.Engine.GetRuleEvaluation("rule").Reason);
        Assert.Equal(0, f.Scheduler.Pending); Assert.Equal(0, executor.Count);
        f.Scheduler.Advance(TimeSpan.FromHours(1)); f.Update(true); await f.Engine.WaitForIdleAsync();
        Assert.Equal(0, executor.Count);
        f.Update(false); f.Update(true); var call = await executor.NextAsync();
        f.Clock.LocalTimeZone = TimeZoneInfo.CreateCustomTimeZone("Synthetic +02", TimeSpan.FromHours(2), "Synthetic", "Synthetic");
        Assert.False(call.Token.IsCancellationRequested);
        call.Complete(); await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        f.Update(false); f.Update(true); await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task DelayCrossingEndIsDiscardedAndOutsideStartNeverWaitsForOpening()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        f.Clock.Advance(TimeSpan.FromSeconds(50));
        f.Engine.SetRule(f.Rule(command: true, timing: new(minimumDuration: TimeSpan.FromSeconds(20), timeWindow: new(0, 1, 127))));
        f.Engine.StartExecution(); f.Update(true); Assert.Equal(1, f.Scheduler.Pending);
        f.Scheduler.Advance(TimeSpan.FromSeconds(20)); await f.Engine.WaitForIdleAsync();
        Assert.Equal(0, executor.Count); Assert.Equal(0, f.Scheduler.Pending);
        Assert.Contains("OutsideTimeWindow", f.Engine.GetRuleEvaluation("rule").Reason);
        f.Engine.SetRule(f.Rule(version: 2, command: true, timing: new(minimumDuration: TimeSpan.FromHours(1), timeWindow: new(120, 180, 127))));
        f.Update(false); f.Update(true); Assert.Equal(0, f.Scheduler.Pending);
        f.Scheduler.Advance(TimeSpan.FromHours(2)); await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task AcceptedButNotDispatchedCommandRechecksCurrentZone()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        var second = new ResourceKey("output", "second", ResourceScope.Machine);
        var source = f.Engine.RegisterSource("second-output", [second]);
        f.Engine.ApplySnapshot(source, 0, [Observe(second, false)]);
        f.Engine.SetRule(f.Rule("hold", input: Noise, command: true));
        f.Engine.SetRule(f.Rule(command: true, output: second, timing: new(timeWindow: new(0, 60, 127))));
        f.Engine.StartExecution(new(concurrency: 1));
        f.Update(true, Noise); var held = await executor.NextAsync();
        f.Update(true); Assert.Equal(1, f.Engine.PendingExecutions);
        f.Clock.LocalTimeZone = TimeZoneInfo.CreateCustomTimeZone("Synthetic +02", TimeSpan.FromHours(2), "Synthetic", "Synthetic");
        held.Complete(); await f.Engine.WaitForIdleAsync();
        Assert.Equal(1, executor.Count); Assert.Contains("OutsideTimeWindow", f.Engine.GetDecision(second).Reason);
        f.Clock.LocalTimeZone = TimeZoneInfo.Utc; f.Update(false); f.Update(true);
        (await executor.NextAsync()).Complete(); await f.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task EventCursorConsumesOutsideEventsAndBaselineAndPauseNeverReplay()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        var schema = new ValueSchema("event-count", 1); var key = new ResourceKey("input", "event", ResourceScope.Machine);
        f.Engine.RegisterCapability(new(Binding("input", schema: schema).Descriptor, ScalarKind.Number, ScalarKind.Number));
        var source = f.Engine.RegisterSource("events", [key]);
        Observation Sample(int count) => new(key, StateQuality.Known, StateValue.From(schema, count), f.Clock.UtcNow);
        f.Engine.ApplySnapshot(source, 0, [Sample(5)]);
        RuleDefinition Rule(long version) => new("event", version, "owner", ResourceScope.Machine, RuleCondition.Event(key, schema),
            RuleAction.Command(Output, StateValue.From(Schema, true)), timing: new(timeWindow: new(60, 120, 127)));
        f.Engine.SetRule(Rule(1)); f.Engine.StartExecution(); f.Engine.Update(source, 1, Sample(6));
        await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Scheduler.Advance(TimeSpan.FromHours(1)); f.Engine.ApplySnapshot(source, 2, [Sample(8)]);
        await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Engine.Update(source, 3, Sample(9)); (await executor.NextAsync()).Complete(); await f.Engine.WaitForIdleAsync();
        Assert.Equal(1, executor.Count);
        f.Engine.RemoveRule("event"); f.Engine.Update(source, 4, Sample(10)); f.Engine.SetRule(Rule(2));
        await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
    }

    [Fact]
    public async Task CompilerEditorAndSqlitePreserveWindowAndRejectUnsupportedOrOldSchema()
    {
        var compiler = new AutomationTaskCompiler(); compiler.Register(new ProcessRunningTaskDetection()); compiler.Register(new NotificationTaskAction());
        var descriptor = compiler.Catalog.Single(c => c.Kind == TaskCapabilityKind.Action);
        Assert.True(descriptor.SupportsTimeWindow);
        var action = new TaskInvocation(descriptor.Id, 1, descriptor.Fields.Where(f => f.DefaultValue is not null)
            .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("body", "Synthetic reminder"));
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "Synthetic work reminder", true,
            new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "Example.exe")), action,
            SchemaVersion: 2, TimeWindow: new(540, 1080, 62));
        var scope = ResourceScope.Session("owner", 1);
        Assert.Equal(task.TimeWindow, compiler.Compile(task, "owner", scope).Timing.TimeWindow);
        Assert.Throws<ArgumentException>(() => compiler.Compile(task with { SchemaVersion = 1 }, "owner", scope));
        var editor = new TaskEditorState(); editor.Load(task); Assert.False(editor.Dirty); Assert.Equal(task.TimeWindow, editor.Build().TimeWindow);
        editor.TimeWindow = null; Assert.True(editor.Dirty); Assert.Equal(1, editor.Build().SchemaVersion);
        var oldJson = JsonSerializer.Serialize(editor.Build()).Replace(",\"TimeWindow\":null", "");
        Assert.Null(JsonSerializer.Deserialize<AutomationTask>(oldJson)!.TimeWindow);
        var hdr = GameHdrTasks.FromLegacy(new(Guid.NewGuid().ToString("D"), 0, "Synthetic HDR", "Example.exe", "SyntheticDisplay", true, true))
            with { SchemaVersion = 2, TimeWindow = task.TimeWindow };
        Assert.Throws<ArgumentException>(() => GameHdrTasks.CreateCompiler().Compile(hdr, "owner", scope));
        Assert.Throws<ArgumentException>(() => GameHdrTasks.ToLegacy(hdr));
        using var fixture = new AutomationFixture(); Assert.Throws<ArgumentException>(() => fixture.Rule(timing: new(timeWindow: task.TimeWindow)));
        var path = Path.Combine(Path.GetTempPath(), "axiom-time-window-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(task, CancellationToken.None);
            using var reopened = new SqliteAutomationTaskStore(path);
            var saved = Assert.Single(await reopened.ListAsync()); Assert.Equal(task.TimeWindow, saved.TimeWindow); Assert.True(saved.Paused);
            Assert.Equal(task.TimeWindow, compiler.Compile(saved, "owner", scope).Timing.TimeWindow);
        }
        finally { File.Delete(path); }
    }
}
