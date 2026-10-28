using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class ProgramPluginTests
{
    private static readonly ResourceScope Scope = ResourceScope.Session("owner", 1);
    private static (AutomationTaskCompiler Compiler, AutomationTask Task) Configure(bool start = true, bool exited = false)
    {
        var plugin = new ProgramPlugin(); var compiler = new AutomationTaskCompiler();
        foreach (var d in plugin.Detections) compiler.Register(d);
        foreach (var a in plugin.Actions) compiler.Register(a);
        var editor = new TaskEditorState(); editor.Load(null); editor.Title = "程序动作";
        editor.Select(compiler.Catalog.Single(c => c.Id == (exited ? "process.exited" : "process.running")), plugin.Combinations);
        editor.Select(ProgramCapabilities.Describe(start), plugin.Combinations);
        editor.SetParameter(TaskCapabilityKind.Detection, "processNames", "trigger.exe");
        editor.SetParameter(TaskCapabilityKind.Action, "executable", @"C:\Apps\target.exe");
        editor.SetParameter(TaskCapabilityKind.Action, "delaySeconds", "2.5");
        return (compiler, editor.Build());
    }
    [Fact]
    public void RealProgramModuleUsesGenericEditorAndCommandCompilerWithTiming()
    {
        var (compiler, task) = Configure(); var rule = compiler.Compile(task, "owner", Scope);
        Assert.Equal(RuleActionKind.Command, rule.Action.Kind); Assert.False(rule.Action.IsIdempotent);
        Assert.Equal(TimeSpan.FromSeconds(2.5), rule.Timing.MinimumDuration);
        Assert.Equal(@"c:\apps\target.exe", rule.Action.Key.ResourceId);
        var (_, close) = Configure(false, true); var exited = compiler.Compile(close, "owner", Scope);
        Assert.Equal(ConditionKind.Not, exited.Condition.Kind);
        Assert.Equal("process.running", exited.Condition.Dependencies.Single().CapabilityId);
        Assert.Equal("program.close", exited.Action.Key.CapabilityId);
    }
    [Fact]
    public void InvalidPathsUnknownFieldsAndUnsafeShellTargetsAreRejected()
    {
        var (compiler, task) = Configure();
        foreach (var path in new[] { "relative.exe", @"\\server\app.exe", @"C:\Windows\cmd.exe", @"C:\Apps\Axiom.exe", @"C:\Apps\script.ps1" })
            Assert.Throws<ArgumentException>(() => compiler.Compile(task with { Action = task.Action with { Parameters = task.Action.Parameters.SetItem("executable", path) } }, "owner", Scope));
        foreach (var delay in new[] { "-1", "86401", "NaN" })
            Assert.Throws<ArgumentException>(() => compiler.Compile(task with { Action = task.Action with { Parameters = task.Action.Parameters.SetItem("delaySeconds", delay) } }, "owner", Scope));
        Assert.Throws<ArgumentException>(() => ProgramCapabilities.Validate(task.Action with { Parameters = task.Action.Parameters.Add("shell", "true") }));
    }
    [Fact]
    public void StartupProtectionIsExplicitPreservesOldLaunchesAndRejectsFractionalOrUnboundedDurations()
    {
        var (compiler, task) = Configure();
        Assert.Equal("normal", ProgramCapabilities.Value(task.Action, "window"));
        Assert.Equal("15", ProgramCapabilities.Value(task.Action, "startupProtectionSeconds"));
        var guarded = task with { Action = task.Action with { Parameters = task.Action.Parameters
            .SetItem("window", "startup-hidden").SetItem("startupProtectionSeconds", "20") } };
        compiler.Compile(guarded, "owner", Scope);
        foreach (var seconds in new[] { "0", "21", "1.5", "NaN", "" })
            Assert.ThrowsAny<ArgumentException>(() => ProgramCapabilities.Validate(guarded.Action with
                { Parameters = guarded.Action.Parameters.SetItem("startupProtectionSeconds", seconds) }));
        var editor = new TaskEditorState(); editor.Load(guarded);
        var rebuilt = editor.Build();
        Assert.Equal("startup-hidden", rebuilt.Action.Parameters["window"]);
        Assert.Equal("20", rebuilt.Action.Parameters["startupProtectionSeconds"]);
        // Existing hidden launches keep their original mode and do not silently opt into injection.
        Assert.Equal("hidden", ProgramCapabilities.Value(task.Action with
            { Parameters = task.Action.Parameters.SetItem("window", "hidden") }, "window"));
    }
    [Fact]
    public async Task GenericStorageSurvivesReopenPreservesParametersAndRejectsStaleEdits()
    {
        var (_, task) = Configure(); task = task with { Action = task.Action with { Parameters = task.Action.Parameters.SetItem("arguments", "--profile \"two words\"") } };
        var dir = Path.Combine(Path.GetTempPath(), "axiom-program-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tasks.db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path))
            {
                var saved = await store.SaveAsync(task, default); Assert.Equal(1, saved.Version);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(task, default));
                await store.SaveAsync(saved with { Paused = true }, default);
            }
            using (var store = new SqliteAutomationTaskStore(path))
            {
                var saved = Assert.Single(await store.ListAsync()); Assert.Equal(2, saved.Version); Assert.True(saved.Paused);
                Assert.Equal("--profile \"two words\"", saved.Action.Parameters["arguments"]);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(saved.Id, 1, default));
                await store.DeleteAsync(saved.Id, 2, default); Assert.Empty(await store.ListAsync());
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact(Timeout = 10000)]
    public async Task DelayedCommandCancelsOnConditionEndAndPauseAndDoesNotReplayBaseline()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        f.Update(true); f.Engine.SetRule(f.Rule(command: true, timing: new(minimumDuration: TimeSpan.FromSeconds(2))));
        f.Engine.StartExecution(); f.Scheduler.Advance(TimeSpan.FromSeconds(3)); await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Update(false); f.Update(true); f.Scheduler.Advance(TimeSpan.FromSeconds(1)); f.Update(false);
        f.Scheduler.Advance(TimeSpan.FromSeconds(3)); await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Update(true); f.Engine.SetRule(new("rule", 2, "owner", ResourceScope.Machine, f.Rule().Condition, f.Rule(command: true).Action, enabled: false));
        f.Scheduler.Advance(TimeSpan.FromSeconds(3)); await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Engine.SetRule(f.Rule(version: 3, command: true, timing: new(minimumDuration: TimeSpan.FromSeconds(2))));
        f.Update(false); f.Update(true); f.Scheduler.Advance(TimeSpan.FromSeconds(2));
        var call = await executor.NextAsync(); call.Complete(); await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        f.Scheduler.Advance(TimeSpan.FromDays(1)); Assert.Equal(1, executor.Count);
    }
}
