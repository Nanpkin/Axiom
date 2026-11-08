using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class ProgramPluginTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void StartupFailureNamesTargetPhaseAndActualProcessState()
    {
        var reason = Axiom.Providers.Windows.WindowsStartupWindowGuard.FailureReason(@"C:\Apps\target.exe", 123, 1460,
            new() { Stage = 7, Resumed = 1, Alive = 1 });
        Assert.StartsWith("target.exe（PID 123）：解除启动保护未完成：", reason);
        Assert.Contains("（1460）", reason);
        Assert.EndsWith("目标进程仍在运行，但尚未确认正常就绪。", reason);
        Assert.DoesNotContain("。。", reason);
        reason = Axiom.Providers.Windows.WindowsStartupWindowGuard.FailureReason(@"C:\Apps\target.exe", 123, 1460,
            new() { Stage = 3 });
        Assert.Contains("初始化进程加载器未完成", reason);
        Assert.EndsWith("目标主线程尚未运行，本次创建的进程已结束。", reason);
    }
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
    public void StartupTrayOptionDefaultsOffForExistingTasksAndValidatesBooleanWithoutChangingClose()
    {
        var (compiler, task) = Configure();
        var old = task with { Action = task.Action with { Parameters = task.Action.Parameters.Remove("hideTrayIcon") } };
        Assert.Equal("false", ProgramCapabilities.Value(old.Action, "hideTrayIcon"));
        compiler.Compile(old, "owner", Scope);
        var enabled = old with { Action = old.Action with { Parameters = old.Action.Parameters.Add("hideTrayIcon", "true") } };
        compiler.Compile(enabled, "owner", Scope);
        var editor = new TaskEditorState(); editor.Load(enabled);
        Assert.Equal("true", editor.Build().Action.Parameters["hideTrayIcon"]);
        var field = Assert.Single(ProgramCapabilities.Describe(true).Fields, f => f.Id == "hideTrayIcon");
        Assert.Equal(TaskFieldKind.Boolean, field.Kind); Assert.Equal("false", field.DefaultValue);
        foreach (var invalid in new[] { "", "True", "1", "yes" })
            Assert.Throws<ArgumentException>(() => ProgramCapabilities.Validate(enabled.Action with
                { Parameters = enabled.Action.Parameters.SetItem("hideTrayIcon", invalid) }));
        var (_, close) = Configure(false);
        Assert.DoesNotContain(ProgramCapabilities.Describe(false).Fields, f => f.Id == "hideTrayIcon");
        Assert.Throws<ArgumentException>(() => ProgramCapabilities.ValidateTargets(close.Action with
            { Parameters = close.Action.Parameters.Add("hideTrayIcon", "true") }));
    }
    [Fact]
    public void StartupTrayTargetDefaultsToLaunchPathAndExplicitLauncherTargetRoundTripsStrictly()
    {
        var (compiler, task) = Configure();
        var old = task.Action with { Parameters = task.Action.Parameters.Remove("hideTrayIcon").Remove("trayExecutable") };
        Assert.Equal("", ProgramCapabilities.Value(old, "trayExecutable"));
        Assert.Equal(@"C:\Apps\target.exe", ProgramCapabilities.TrayTarget(old));
        compiler.Compile(task with { Action = old }, "owner", Scope);
        var explicitTarget = old with { Parameters = old.Parameters.Add("hideTrayIcon", "true").Add("trayExecutable", @"C:\Apps\Main\TrayOwner.exe") };
        Assert.Equal(@"C:\Apps\target.exe", ProgramCapabilities.Validate(explicitTarget));
        Assert.Equal(@"C:\Apps\Main\TrayOwner.exe", ProgramCapabilities.TrayTarget(explicitTarget));
        compiler.Compile(task with { Action = explicitTarget }, "owner", Scope);
        var editor = new TaskEditorState(); editor.Load(task with { Action = explicitTarget });
        Assert.Equal(@"C:\Apps\Main\TrayOwner.exe", editor.Build().Action.Parameters["trayExecutable"]);
        var field = Assert.Single(ProgramCapabilities.Describe(true).Fields, f => f.Id == "trayExecutable");
        Assert.Equal(TaskFieldKind.FilePath, field.Kind); Assert.True(field.BrowseLocalPath); Assert.False(field.Required);
        foreach (var enabled in new[] { "true", "false" })
            foreach (var invalid in new[] { "relative.exe", @"\\server\Tray.exe", @"C:\Apps\script.ps1", @"C:\Windows\cmd.exe", " ", "C:\\Apps\\A.exe\nC:\\Apps\\B.exe" })
                Assert.Throws<ArgumentException>(() => ProgramCapabilities.ValidateTargets(explicitTarget with
                { Parameters = explicitTarget.Parameters.SetItem("hideTrayIcon", enabled).SetItem("trayExecutable", invalid) }));
        var (_, close) = Configure(false);
        Assert.DoesNotContain(ProgramCapabilities.Describe(false).Fields, f => f.Id == "trayExecutable");
        Assert.Throws<ArgumentException>(() => ProgramCapabilities.ValidateTargets(close.Action with
            { Parameters = close.Action.Parameters.Add("trayExecutable", @"C:\Apps\TrayOwner.exe") }));
    }
    [Fact]
    public void StartupTrayTargetImportKeepsEmptyDefaultAndOnlyResolvesObservedAliases()
    {
        var (compiler, task) = Configure();
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UnixEpoch);
        recorder.Add(new(DateTimeOffset.UnixEpoch, "process-start", @"C:\Apps\target.exe"));
        var alias = Assert.Single(recorder.Session.Objects).Id;
        var plugin = new ProgramPlugin();
        var catalog = new AutomationSnapshot([], compiler.Catalog, plugin.Combinations, true, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, []);
        var detection = task.Detection with { Parameters = task.Detection.Parameters.SetItem("processNames", alias) };
        var action = task.Action with { Parameters = task.Action.Parameters.SetItem("executable", alias).SetItem("trayExecutable", "") };
        UsageImport Proposal(TaskInvocation invocation) => new(1, recorder.Session.Id, "existing", "synthetic", "synthetic", detection, invocation);
        AutomationTask Import(TaskInvocation invocation) => UsageAnalysis.Import(JsonSerializer.Serialize(Proposal(invocation)), recorder.Session, catalog).Draft!;
        var empty = Import(action);
        Assert.Equal("", empty.Action.Parameters["trayExecutable"]);
        Assert.Equal(@"C:\Apps\target.exe", ProgramCapabilities.TrayTarget(empty.Action));
        var observed = Import(action with { Parameters = action.Parameters.SetItem("trayExecutable", alias) });
        Assert.Equal(@"C:\Apps\target.exe", observed.Action.Parameters["trayExecutable"]);
        Assert.Throws<ArgumentException>(() => Import(action with
            { Parameters = action.Parameters.SetItem("trayExecutable", @"C:\Apps\Unobserved.exe") }));
    }
    [Fact]
    public async Task StartupTrayOptionHidesEvenSkippedLaunchAndSeparatesNoIconFromSuccess()
    {
        var skipped = new ProgramControlResult(true, "程序已运行，按配置跳过启动。") { Skipped = true };
        var hideCalls = 0;
        Task<ProgramControlResult> Hide(TimeSpan wait, CancellationToken token)
        {
            hideCalls++; Assert.Equal(TimeSpan.FromSeconds(30), wait);
            return Task.FromResult(new ProgramControlResult(true, "已隐藏 1 个图标。"));
        }
        var unchanged = await WindowsProgramStart.ExecuteAsync(false, _ => Task.FromResult(skipped), Hide, default);
        Assert.Same(skipped, unchanged); Assert.Equal(0, hideCalls);
        var hidden = await WindowsProgramStart.ExecuteAsync(true, _ => Task.FromResult(skipped), Hide, default);
        Assert.True(hidden.Success, hidden.Reason); Assert.False(hidden.Skipped); Assert.Equal(1, hideCalls);
        Assert.Contains("跳过启动", hidden.Reason); Assert.Contains("已隐藏", hidden.Reason);
        var startFailed = new ProgramControlResult(false, "程序启动失败。");
        Assert.Same(startFailed, await WindowsProgramStart.ExecuteAsync(true, _ => Task.FromResult(startFailed), Hide, default));
        Assert.Equal(1, hideCalls);
        var noIcon = await WindowsProgramStart.ExecuteAsync(true, _ => Task.FromResult(new ProgramControlResult(true, "已启动程序。")),
            (_, _) => Task.FromResult(new ProgramControlResult(true, "未发现图标，无需处理。") { Skipped = true }), default);
        Assert.False(noIcon.Success); Assert.Contains("已启动程序", noIcon.Reason); Assert.Contains("隐藏未确认", noIcon.Reason);
        var denied = await WindowsProgramStart.ExecuteAsync(true, _ => Task.FromResult(skipped),
            (_, _) => throw new UnauthorizedAccessException("合成权限拒绝"), default);
        Assert.False(denied.Success); Assert.Contains("合成权限拒绝", denied.Reason); Assert.Contains("程序仍保留", denied.Reason);
    }
    [Theory]
    [InlineData(0, 30)]
    [InlineData(20, 15)]
    [InlineData(35, 0)]
    public async Task StartupTrayWaitUsesRemainingProgramDeadline(int startupSeconds, int expectedWait)
    {
        var time = new ManualTimeProvider(); var hideCalls = 0;
        var result = await WindowsProgramStart.ExecuteAsync(true, _ =>
        {
            time.Advance(TimeSpan.FromSeconds(startupSeconds));
            return Task.FromResult(new ProgramControlResult(true, "已完成启动。"));
        }, (wait, _) =>
        {
            hideCalls++; Assert.Equal(TimeSpan.FromSeconds(expectedWait), wait);
            return Task.FromResult(new ProgramControlResult(true, "已隐藏。"));
        }, default, time);
        Assert.Equal(expectedWait > 0, result.Success); Assert.Equal(expectedWait > 0 ? 1 : 0, hideCalls);
    }
    [Fact]
    public async Task CancellationAfterLaunchDoesNotBeginTrayWriteOrRelaunch()
    {
        using var stop = new CancellationTokenSource(); var starts = 0; var hides = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsProgramStart.ExecuteAsync(true, _ =>
        {
            starts++; stop.Cancel(); return Task.FromResult(new ProgramControlResult(true, "已启动程序。"));
        }, (_, _) => { hides++; throw new InvalidOperationException("不得隐藏"); }, stop.Token));
        Assert.Equal(1, starts); Assert.Equal(0, hides);
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
