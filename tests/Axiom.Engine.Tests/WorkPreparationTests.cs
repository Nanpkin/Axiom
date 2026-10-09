using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class WorkPreparationTests
{
    private static TaskInvocation Invocation(string targets, string delay = "0") => new(WorkPreparationCapabilities.Prepare, 1,
        ImmutableDictionary<string, string>.Empty.Add("steps", targets).Add("delaySeconds", delay));
    private static (string Kind, string Target)[] Targets => [("program", Path.Combine(Path.GetTempPath(), "SyntheticTool.exe")), ("url", "https://example.invalid/second"), ("url", "https://example.invalid/third")];

    [Fact]
    public async Task ConfigurationUsesExistingSafeTypesAndManualOnlyImportWithPausedRegisteredTemplate()
    {
        var path = Path.Combine(Path.GetTempPath(), "SyntheticBatch");
        var invocation = Invocation($"程序 | {path}.exe\r\n目录 | {path}\r\n网页 | https://example.invalid/work\r\n文档 | {path}.pdf", "5");
        var parsed = WorkPreparationCapabilities.Validate(invocation);
        Assert.Equal(new[] { "program", "folder", "url", "document" }, parsed.Steps.Select(t => t.Kind)); Assert.Equal(5, parsed.Delay);
        foreach (var suffix in new[] { ".exe", ".cmd", ".ps1", ".lnk", ".docm", ".pdf:stream" })
            Assert.Throws<ArgumentException>(() => WorkPreparationCapabilities.Validate(Invocation("网页 | https://example.invalid/safe\n文档 | " + path + suffix)));
        foreach (var text in new[] { "目录 | //synthetic-server/share", "目录 | \\\\synthetic-server\\share", "网页 | javascript:alert(1)", "网页 | https://user:password@example.invalid/", "程序 | " + path + ".cmd", "网页 | https://example.invalid/\t", "网页 | https://example.invalid/\n", "网页 | https://example.invalid/" + new string('x', 256), string.Join("\n", Enumerable.Range(1, 9).Select(i => "网页 | https://example.invalid/" + i)) })
            Assert.Throws<ArgumentException>(() => WorkPreparationCapabilities.Validate(Invocation(text)));
        foreach (var file in new[] { "cmd.exe", "pwsh.exe", "Axiom.exe", "Synthetic.cmd", "Synthetic.exe:stream.exe" })
            Assert.Throws<ArgumentException>(() => WorkPreparationCapabilities.Validate(Invocation("程序 | " + Path.Combine(Path.GetTempPath(), file))));
        var modules = BuiltInAutomationPlugins.Create(); Assert.Single(modules.OfType<WorkPreparationPlugin>());
        Assert.DoesNotContain(modules.SelectMany(m => m.Combinations), c => c.ActionId == WorkPreparationCapabilities.Prepare && c.DetectionId is "process.running" or "process.exited" or ForegroundCapabilities.Active);
        Assert.True(new WorkPreparationTaskAction().SupportsTimeWindow);
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "shortcut-work-preparation");
        Assert.True(template.Draft.Paused); Assert.Equal(TaskFieldKind.TextList, WorkPreparationCapabilities.Descriptor.Fields[0].Kind);
        Assert.True(WorkPreparationCapabilities.Descriptor.Fields[0].EditAsList);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        var chords = new TaskInvocation(HotkeyCapabilities.Pressed, 1, ImmutableDictionary<string, string>.Empty.Add("chords", "Ctrl+Alt+F9"));
        Assert.NotNull(compiler.Compile(template.Draft with { Detection = chords, Action = invocation }, "owner", ResourceScope.Session("owner", 1)));
        Assert.Equal(TimeSpan.FromSeconds(5), new WorkPreparationTaskAction().Timing(invocation).MinimumDuration);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "打开工作目标", "合成建议", chords, Invocation(""));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(new[] { "工作准备步骤" }, UsageAnalysis.MissingManualFields(draft, catalog));
        foreach (var invalid in new[] { invocation, Invocation("", "0.5"), Invocation("", "-1"), Invocation("", "86401"), Invocation("") with { Parameters = Invocation("").Parameters.Add("script", "") } })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = invalid }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var completed = draft with { Action = invocation, SchemaVersion = 2, TimeWindow = new(540, 1080, 62) };
        Assert.Equal(completed.TimeWindow, compiler.Compile(completed, "owner", ResourceScope.Session("owner", 1)).Timing.TimeWindow);
        var database = Path.Combine(Path.GetTempPath(), "axiom-work-prepare-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteAutomationTaskStore(database);
            await store.SaveAsync(completed, default);
            var saved = Assert.Single(await store.ListAsync());
            Assert.True(saved.Paused); Assert.Equal(invocation.Parameters["steps"], saved.Action.Parameters["steps"]);
            Assert.Equal(completed.TimeWindow, saved.TimeWindow);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(database + suffix); }
    }

    [Fact]
    public void AllPreflightChecksPrecedeRequestsAndAnyFailureStopsLaterTargetsWithoutLeakingTargets()
    {
        var rejected = new Api { PreflightRefusal = 2 }; var rejectedProgress = new WorkPreparationProgress(3);
        Assert.False(WorkPreparationOperation.Apply(Targets, rejected, rejectedProgress, default).Success);
        Assert.Empty(rejected.Opened); Assert.Equal(2, rejected.Checked.Count); Assert.Contains("完成0/3", rejectedProgress.Describe());
        var accepted = new Api { Skip = 1 }; var allProgress = new WorkPreparationProgress(3);
        Assert.True(WorkPreparationOperation.Apply(Targets, accepted, allProgress, default).Success);
        Assert.Equal(Targets.Select(t => t.Target), accepted.Opened); Assert.Equal(3, accepted.CheckedBeforeFirstOpen); Assert.Contains("完成3/3", allProgress.Describe()); Assert.Contains("已运行跳过1步", allProgress.Describe());
        var stopped = new Api { OpenRefusal = 2 }; var partial = new WorkPreparationProgress(3);
        var result = WorkPreparationOperation.Apply(Targets, stopped, partial, default);
        Assert.False(result.Success); Assert.Equal(2, stopped.Opened.Count); Assert.Contains("完成1/3", partial.Describe());
        Assert.Contains("第2步", result.Reason); Assert.Contains("第2步可能已受理", partial.Describe());
        Assert.Contains("合成访问拒绝", result.Reason);
        Assert.DoesNotContain("example.invalid", result.Reason + partial.Describe()); Assert.Equal(3, stopped.CheckedBeforeFirstOpen);
    }

    [Fact]
    public void CancellationPreventsUnsentRequestsAndPreservesAlreadyAcceptedPartialProgress()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Api();
        Assert.False(WorkPreparationOperation.Apply(Targets, untouched, new(3), before.Token).Success);
        Assert.Empty(untouched.Checked); Assert.Empty(untouched.Opened);
        using var preflight = new CancellationTokenSource(); var canceled = new Api { AfterCheck = preflight.Cancel };
        Assert.False(WorkPreparationOperation.Apply(Targets, canceled, new(3), preflight.Token).Success); Assert.Empty(canceled.Opened);
        using var after = new CancellationTokenSource(); var partial = new Api { AfterOpen = after.Cancel }; var progress = new WorkPreparationProgress(3);
        Assert.False(WorkPreparationOperation.Apply(Targets, partial, progress, after.Token).Success);
        Assert.Single(partial.Opened); Assert.Contains("完成1/3", progress.Describe()); Assert.Contains("不关闭", progress.Describe());
    }

    [Fact(Timeout = 10000)]
    public async Task WholeBatchTimeoutReportsPendingItemAndRetainsAdmissionUntilNativeReturn()
    {
        var commands = new BoundedNativeCommand("打开目标", TimeSpan.FromSeconds(8)); var time = new ManualTimeProvider();
        var timerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        time.TimerCreated = _ => timerReady.TrySetResult(); using var release = new ManualResetEventSlim();
        var api = new Api(); var progress = new WorkPreparationProgress(3);
        api.AfterOpen = () => { if (api.Opened.Count == 2) { entered.SetResult(); release.Wait(); } };
        var work = commands.RunAsync(token => WorkPreparationOperation.Apply(Targets, api, progress, token), default, time);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await timerReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            time.Advance(TimeSpan.FromSeconds(8)); Assert.False((await work).Success);
            Assert.Contains("完成1/3", progress.Describe()); Assert.Contains("第2步可能已受理", progress.Describe());
            Assert.False(commands.DrainAsync().IsCompleted);
            Assert.False((await commands.RunAsync(_ => throw new InvalidOperationException("must not overlap"), default)).Success);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(2, api.Opened.Count); Assert.Contains("完成2/3", progress.Describe());
    }

    [Fact]
    public void ExceptionDuringStartedStepKeepsUnconfirmedOrdinalAndStopsWithoutLeakingTargets()
    {
        var api = new Api { AfterOpen = () => throw new InvalidOperationException("private target detail") };
        var progress = new WorkPreparationProgress(3);
        var result = WorkPreparationOperation.Apply(Targets, api, progress, default);
        Assert.False(result.Success); Assert.Single(api.Opened);
        Assert.Contains("第1步可能已受理", progress.Describe()); Assert.Contains("完成0/3", progress.Describe());
        Assert.DoesNotContain("private target", result.Reason); Assert.DoesNotContain("SyntheticTool", result.Reason + progress.Describe());
        var blocked = new Api { AfterCheck = () => throw new InvalidOperationException("private preflight") };
        var before = new WorkPreparationProgress(3);
        Assert.False(WorkPreparationOperation.Apply(Targets, blocked, before, default).Success);
        Assert.Empty(blocked.Opened); Assert.Contains("第1步预检未确认", before.Describe());
    }

    private sealed class Api : IWorkPreparationApi
    {
        internal readonly List<string> Checked = [], Opened = [];
        internal int PreflightRefusal, OpenRefusal, Skip, CheckedBeforeFirstOpen; internal Action? AfterCheck, AfterOpen;
        public bool CanExecute(string kind, string target, CancellationToken token) { Checked.Add(target); AfterCheck?.Invoke(); return Checked.Count != PreflightRefusal; }
        public WorkPreparationStepResult Execute(string kind, string target, CancellationToken token)
        { if (Opened.Count == 0) CheckedBeforeFirstOpen = Checked.Count; Opened.Add(target); AfterOpen?.Invoke(); return new(Opened.Count != OpenRefusal, Opened.Count == Skip, "合成访问拒绝"); }
    }
}
