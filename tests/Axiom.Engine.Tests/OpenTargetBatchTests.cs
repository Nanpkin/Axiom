using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class OpenTargetBatchTests
{
    private static TaskInvocation Invocation(string targets, string delay = "0") => new(OpenTargetBatchCapabilities.Open, 1,
        ImmutableDictionary<string, string>.Empty.Add("targets", targets).Add("delaySeconds", delay));
    private static (string Kind, string Target)[] Targets => [("url", "https://example.invalid/first"), ("url", "https://example.invalid/second"), ("url", "https://example.invalid/third")];

    [Fact]
    public async Task ConfigurationUsesExistingSafeTypesAndManualOnlyImportWithPausedRegisteredTemplate()
    {
        var path = Path.Combine(Path.GetTempPath(), "SyntheticBatch");
        var invocation = Invocation($"目录 | {path}\r\n网页 | https://example.invalid/work\r\n文档 | {path}.pdf", "5");
        var parsed = OpenTargetBatchCapabilities.Validate(invocation);
        Assert.Equal(new[] { "folder", "url", "document" }, parsed.Targets.Select(t => t.Kind)); Assert.Equal(5, parsed.Delay);
        foreach (var suffix in new[] { ".exe", ".cmd", ".ps1", ".lnk", ".docm", ".pdf:stream" })
            Assert.Throws<ArgumentException>(() => OpenTargetBatchCapabilities.Validate(Invocation("网页 | https://example.invalid/safe\n文档 | " + path + suffix)));
        foreach (var text in new[] { "目录 | //synthetic-server/share", "目录 | \\\\synthetic-server\\share", "网页 | javascript:alert(1)", "网页 | https://user:password@example.invalid/", "程序 | " + path, "网页 | https://example.invalid/\t", "网页 | https://example.invalid/\n", "网页 | https://example.invalid/" + new string('x', 256), string.Join("\n", Enumerable.Range(1, 9).Select(i => "网页 | https://example.invalid/" + i)) })
            Assert.Throws<ArgumentException>(() => OpenTargetBatchCapabilities.Validate(Invocation(text)));
        var modules = BuiltInAutomationPlugins.Create(); Assert.Single(modules.OfType<OpenTargetBatchPlugin>());
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "shortcut-workspace");
        Assert.True(template.Draft.Paused); Assert.Equal(TaskFieldKind.TextList, OpenTargetBatchCapabilities.Descriptor.Fields[0].Kind);
        Assert.True(OpenTargetBatchCapabilities.Descriptor.Fields[0].EditAsList);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        var chords = new TaskInvocation(HotkeyCapabilities.Pressed, 1, ImmutableDictionary<string, string>.Empty.Add("chords", "Ctrl+Alt+F9"));
        Assert.NotNull(compiler.Compile(template.Draft with { Detection = chords, Action = invocation }, "owner", ResourceScope.Session("owner", 1)));
        Assert.Equal(TimeSpan.FromSeconds(5), new OpenTargetBatchTaskAction().Timing(invocation).MinimumDuration);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "打开工作目标", "合成建议", chords, Invocation(""));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(new[] { "工作目标列表" }, UsageAnalysis.MissingManualFields(draft, catalog));
        foreach (var invalid in new[] { invocation, Invocation("", "0.5"), Invocation("", "-1"), Invocation("", "86401"), Invocation("") with { Parameters = Invocation("").Parameters.Add("script", "") } })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = invalid }), recorder.Session, catalog));
        var completed = draft with { Action = invocation };
        Assert.NotNull(compiler.Compile(completed, "owner", ResourceScope.Session("owner", 1)));
        var database = Path.Combine(Path.GetTempPath(), "axiom-open-batch-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new SqliteAutomationTaskStore(database);
            await store.SaveAsync(completed, default);
            var saved = Assert.Single(await store.ListAsync());
            Assert.True(saved.Paused); Assert.Equal(invocation.Parameters["targets"], saved.Action.Parameters["targets"]);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(database + suffix); }
    }

    [Fact]
    public void AllPreflightChecksPrecedeRequestsAndAnyFailureStopsLaterTargetsWithoutLeakingTargets()
    {
        var rejected = new Api { PreflightRefusal = 2 }; var rejectedProgress = new OpenTargetBatchProgress(3);
        Assert.False(OpenTargetBatchOperation.Apply(Targets, rejected, rejectedProgress, default).Success);
        Assert.Empty(rejected.Opened); Assert.Equal(2, rejected.Checked.Count); Assert.Contains("受理0/3", rejectedProgress.Describe());
        var accepted = new Api(); var allProgress = new OpenTargetBatchProgress(3);
        Assert.True(OpenTargetBatchOperation.Apply(Targets, accepted, allProgress, default).Success);
        Assert.Equal(Targets.Select(t => t.Target), accepted.Opened); Assert.Equal(3, accepted.CheckedBeforeFirstOpen); Assert.Contains("受理3/3", allProgress.Describe());
        var stopped = new Api { OpenRefusal = 2 }; var partial = new OpenTargetBatchProgress(3);
        var result = OpenTargetBatchOperation.Apply(Targets, stopped, partial, default);
        Assert.False(result.Success); Assert.Equal(2, stopped.Opened.Count); Assert.Contains("受理1/3", partial.Describe());
        Assert.DoesNotContain("example.invalid", result.Reason + partial.Describe()); Assert.Equal(3, stopped.CheckedBeforeFirstOpen);
    }

    [Fact]
    public void CancellationPreventsUnsentRequestsAndPreservesAlreadyAcceptedPartialProgress()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Api();
        Assert.False(OpenTargetBatchOperation.Apply(Targets, untouched, new(3), before.Token).Success);
        Assert.Empty(untouched.Checked); Assert.Empty(untouched.Opened);
        using var preflight = new CancellationTokenSource(); var canceled = new Api { AfterCheck = preflight.Cancel };
        Assert.False(OpenTargetBatchOperation.Apply(Targets, canceled, new(3), preflight.Token).Success); Assert.Empty(canceled.Opened);
        using var after = new CancellationTokenSource(); var partial = new Api { AfterOpen = after.Cancel }; var progress = new OpenTargetBatchProgress(3);
        Assert.False(OpenTargetBatchOperation.Apply(Targets, partial, progress, after.Token).Success);
        Assert.Single(partial.Opened); Assert.Contains("受理1/3", progress.Describe()); Assert.Contains("不自动关闭", progress.Describe());
    }

    [Fact(Timeout = 10000)]
    public async Task WholeBatchTimeoutReportsPendingItemAndRetainsAdmissionUntilNativeReturn()
    {
        var commands = new BoundedNativeCommand("打开目标", TimeSpan.FromSeconds(8)); var time = new ManualTimeProvider();
        var timerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        time.TimerCreated = _ => timerReady.TrySetResult(); using var release = new ManualResetEventSlim();
        var api = new Api(); var progress = new OpenTargetBatchProgress(3);
        api.AfterOpen = () => { if (api.Opened.Count == 2) { entered.SetResult(); release.Wait(); } };
        var work = commands.RunAsync(token => OpenTargetBatchOperation.Apply(Targets, api, progress, token), default, time);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await timerReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            time.Advance(TimeSpan.FromSeconds(8)); Assert.False((await work).Success);
            Assert.Contains("受理1/3", progress.Describe()); Assert.Contains("第2项可能已受理", progress.Describe());
            Assert.False(commands.DrainAsync().IsCompleted);
            Assert.False((await commands.RunAsync(_ => throw new InvalidOperationException("must not overlap"), default)).Success);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(2, api.Opened.Count); Assert.Contains("受理2/3", progress.Describe());
    }

    private sealed class Api : IOpenTargetBatchApi
    {
        internal readonly List<string> Checked = [], Opened = [];
        internal int PreflightRefusal, OpenRefusal, CheckedBeforeFirstOpen; internal Action? AfterCheck, AfterOpen;
        public bool CanOpen(string kind, string target, CancellationToken token) { Checked.Add(target); AfterCheck?.Invoke(); return Checked.Count != PreflightRefusal; }
        public bool Open(string kind, string target, CancellationToken token)
        { if (Opened.Count == 0) CheckedBeforeFirstOpen = Checked.Count; Opened.Add(target); AfterOpen?.Invoke(); return Opened.Count != OpenRefusal; }
    }
}
