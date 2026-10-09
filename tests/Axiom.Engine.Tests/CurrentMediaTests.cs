using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class CurrentMediaTests
{
    private static TaskInvocation Invocation(string operation = "pause", string delay = "0") => new(CurrentMediaCapabilities.Control, 1,
        new Dictionary<string, string> { ["operation"] = operation, ["delaySeconds"] = delay }.ToImmutableDictionary());

    [Fact]
    public void RealRegistrationUsesOneCurrentMediaResourceEngineDelayAndTimeWindow()
    {
        var compiler = new CurrentMediaTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(compiler.SupportsTimeWindow);
        foreach (var operation in new[] { "pause", "play", "previous", "next" })
        {
            Assert.Equal("windows-current-media-session", compiler.Compile(Invocation(operation), scope).Key.ResourceId);
            Assert.Equal(compiler.Compile(Invocation(), scope).Key, compiler.Compile(Invocation(operation, "60"), scope).Key);
        }
        Assert.Equal(TimeSpan.FromSeconds(60), compiler.Timing(Invocation(delay: "60")).MinimumDuration);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<CurrentMediaPlugin>());
        var binding = Assert.Single(plugin.Bindings);
        Assert.Equal(RetryCategory.Never, binding.Descriptor.Retry);
        Assert.Equal(ConfirmationSource.ReceiptOnly, binding.Descriptor.Confirmation);
        foreach (var detection in new[] { SystemEventCapabilities.Event, HotkeyCapabilities.Pressed, ForegroundCapabilities.Active, DailyTimeCapabilities.Due })
            Assert.Contains(plugin.Combinations, c => c.DetectionId == detection && c.ActionId == CurrentMediaCapabilities.Control);
        Assert.Contains("不是指定程序", compiler.Descriptor.Fields[0].Hint);
        Assert.Contains("不读取曲目", compiler.Descriptor.Behavior);
        Assert.Contains("关闭Axiom窗口后后台仍", compiler.Descriptor.Behavior);
    }

    [Theory]
    [InlineData("operation", "toggle")]
    [InlineData("operation", "stop")]
    [InlineData("operation", "script")]
    [InlineData("delaySeconds", "-1")]
    [InlineData("delaySeconds", "86401")]
    [InlineData("delaySeconds", "1.5")]
    [InlineData("delaySeconds", " 1")]
    public void UnsupportedOperationsAndInvalidDelayCannotCompile(string field, string value) =>
        Assert.Throws<ArgumentException>(() => CurrentMediaCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem(field, value) }));

    [Theory]
    [InlineData("executable", "C:\\Synthetic\\Player.exe")]
    [InlineData("appUserModelId", "synthetic.player")]
    [InlineData("sessionId", "1")]
    [InlineData("retry", "2")]
    public void TargetIdentityOrRetryCannotBeSmuggledIntoCurrentSessionMode(string field, string value) =>
        Assert.Throws<ArgumentException>(() => CurrentMediaCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add(field, value) }));

    [Theory]
    [InlineData("pause")]
    [InlineData("play")]
    [InlineData("previous")]
    [InlineData("next")]
    public void AllFourRequestsUseTheCapturedCurrentSessionExactlyOnce(string operation)
    {
        using var api = new Api();
        var result = CurrentMediaOperation.Apply(api, operation, default);
        Assert.True(result.Success); Assert.Contains("已接受", result.Reason); Assert.Contains("是否完成未确认", result.Reason);
        Assert.Equal(new[] { operation }, api.Initial.Requests); Assert.Empty(api.Replacement.Requests);
        Assert.Equal(1, api.Captures); Assert.Equal(1, api.Checks); Assert.Equal(1, api.Initial.Reads);
    }

    [Theory]
    [InlineData("pause", (int)CurrentMediaPlaybackStatus.Paused)]
    [InlineData("play", (int)CurrentMediaPlaybackStatus.Playing)]
    public void AlreadyPausedOrPlayingIsExplicitNoOpEvenWhenRepeatControlIsDisabled(string operation, int status)
    {
        using var api = new Api(); api.Initial.Playback = new((CurrentMediaPlaybackStatus)status, false, false, false, false);
        var result = CurrentMediaOperation.Apply(api, operation, default);
        Assert.True(result.Success); Assert.Contains("无需操作", result.Reason); Assert.Contains("未发送重复请求", result.Reason);
        Assert.Empty(api.Initial.Requests);
    }

    [Theory]
    [InlineData("pause", "暂停")]
    [InlineData("play", "播放")]
    [InlineData("previous", "上一项")]
    [InlineData("next", "下一项")]
    public void DisabledControlNeverFallsBackToToggleKeysOrAnotherSession(string operation, string label)
    {
        using var api = new Api(); api.Initial.Playback = new(CurrentMediaPlaybackStatus.Unknown, false, false, false, false);
        var result = CurrentMediaOperation.Apply(api, operation, default);
        Assert.False(result.Success); Assert.Contains($"未启用{label}", result.Reason);
        Assert.Empty(api.Initial.Requests); Assert.Empty(api.Replacement.Requests);
    }

    [Fact]
    public void NoCurrentSessionDoesNotUseAnAvailableReplacement()
    {
        using var api = new Api { Current = null };
        var result = CurrentMediaOperation.Apply(api, "pause", default);
        Assert.False(result.Success); Assert.Contains("没有选中的当前媒体会话", result.Reason);
        Assert.Equal(0, api.Checks); Assert.Equal(0, api.Replacement.Reads); Assert.Empty(api.Replacement.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASelectionChangeOrSwitchBackDuringReadIsRejectedWithoutRetargeting(bool switchBack)
    {
        using var api = new Api();
        api.Initial.AfterRead = () => { api.ChangeTo(api.Replacement); if (switchBack) api.ChangeTo(api.Initial); };
        var result = CurrentMediaOperation.Apply(api, "pause", default);
        Assert.False(result.Success); Assert.Contains("已切换或消失", result.Reason);
        Assert.Empty(api.Initial.Requests); Assert.Empty(api.Replacement.Requests); Assert.Equal(1, api.Captures);
    }

    [Fact]
    public void AChangeAfterFinalCheckStillOnlySendsToTheFixedOldSession()
    {
        using var api = new Api(); api.AfterCheck = () => api.ChangeTo(api.Replacement);
        Assert.True(CurrentMediaOperation.Apply(api, "next", default).Success);
        Assert.Equal(new[] { "next" }, api.Initial.Requests); Assert.Empty(api.Replacement.Requests);
    }

    [Fact]
    public void RefusalIsNotRetriedOrReportedAsObservedPlayback()
    {
        using var api = new Api(); api.Initial.Accepted = false;
        var result = CurrentMediaOperation.Apply(api, "play", default);
        Assert.False(result.Success); Assert.Contains("未接受", result.Reason); Assert.Contains("不自动重试", result.Reason);
        Assert.Single(api.Initial.Requests); Assert.Equal(1, api.Initial.Reads);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("capture")]
    [InlineData("read")]
    [InlineData("check")]
    [InlineData("request")]
    public void CancellationStopsUnsentCommandsAndNeverUndoesAnAlreadySentCommand(string phase)
    {
        using var stop = new CancellationTokenSource(); using var api = new Api();
        if (phase == "before") stop.Cancel();
        if (phase == "capture") api.AfterCapture = stop.Cancel;
        if (phase == "read") api.Initial.AfterRead = stop.Cancel;
        if (phase == "check") api.AfterCheck = stop.Cancel;
        if (phase == "request") api.Initial.AfterRequest = stop.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => CurrentMediaOperation.Apply(api, "pause", stop.Token));
        Assert.Equal(phase == "request" ? 1 : 0, api.Initial.Requests.Count); Assert.Empty(api.Replacement.Requests);
    }

    [Fact(Timeout = 10000)]
    public async Task CancellationKeepsNativeAdmissionUntilOriginalMediaRequestFinishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        using var api = new Api(); api.Initial.AfterRequest = () => { entered.SetResult(); release.Wait(); };
        var commands = new BoundedNativeCommand("Windows当前媒体控制", TimeSpan.FromSeconds(5));
        var request = commands.RunAsync(token => CurrentMediaOperation.Apply(api, "pause", token), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(commands.DrainAsync().IsCompleted);
            Assert.False((await commands.RunAsync(token => CurrentMediaOperation.Apply(api, "next", token), default)).Success);
            Assert.Equal(new[] { "pause" }, api.Initial.Requests);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public void LockPauseTemplateUsesTheRealRegistryAndRemainsUnsavedPaused()
    {
        var plugins = BuiltInAutomationPlugins.Create();
        var catalog = new AutomationSnapshot([], [.. plugins.SelectMany(p => p.Detections.Select(d => d.Descriptor).Concat(p.Actions.Select(a => a.Descriptor)))],
            [.. plugins.SelectMany(p => p.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "lock-pause-current-media");
        Assert.True(template.Draft.Paused); Assert.Equal(0, template.Draft.Version);
        Assert.Equal("locked", template.Draft.Detection.Parameters["event"]);
        Assert.Equal(CurrentMediaCapabilities.Control, template.Draft.Action.CapabilityId);
        Assert.Equal("pause", template.Draft.Action.Parameters["operation"]); Assert.Equal("0", template.Draft.Action.Parameters["delaySeconds"]);
        Assert.DoesNotContain(AutomationTemplates.Build(catalog with { Combinations = [] }), t => t.Id == template.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedRuleAndImportedDraftPreserveCurrentTargetDelayAndTimeWindowWithoutExecuting(bool screenOff)
    {
        var plugins = BuiltInAutomationPlugins.Create(); var compiler = new AutomationTaskCompiler();
        foreach (var plugin in plugins)
        {
            foreach (var detection in plugin.Detections) compiler.Register(detection);
            foreach (var action in plugin.Actions) compiler.Register(action);
        }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. plugins.SelectMany(p => p.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "合成媒体暂停", "合成测试候选",
            screenOff ? new(SessionDisplayCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("state", "off"))
                : new(SystemEventCapabilities.Event, 1, ImmutableDictionary<string, string>.Empty.Add("event", "locked")), Invocation(delay: "15"));
        var imported = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(imported.Paused); Assert.Equal(0, imported.Version);
        foreach (var value in new[] { "1.5", " 1" })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = Invocation(delay: value) }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = Invocation("toggle") }), recorder.Session, catalog));
        var window = new WeeklyTimeWindow(540, 1080, 62);
        var configured = imported with { SchemaVersion = 2, TimeWindow = window };
        var compiled = compiler.Compile(configured, "owner", ResourceScope.Session("owner", 1));
        Assert.Equal(window, compiled.Timing.TimeWindow); Assert.Equal(TimeSpan.FromSeconds(15), compiled.Timing.MinimumDuration);
        var path = Path.Combine(Path.GetTempPath(), "axiom-media-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            AutomationTask saved;
            using (var store = new SqliteAutomationTaskStore(path)) saved = await store.SaveAsync(configured, default);
            using var reopened = new SqliteAutomationTaskStore(path);
            var read = Assert.Single(await reopened.ListAsync());
            Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(read)); Assert.True(read.Paused);
            Assert.Equal("pause", read.Action.Parameters["operation"]); Assert.Equal(window, read.TimeWindow);
            Assert.Equal(compiled.Timing.MinimumDuration, compiler.Compile(read, "owner", ResourceScope.Session("owner", 1)).Timing.MinimumDuration);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private sealed class Api : ICurrentMediaApi
    {
        internal readonly Session Initial = new(), Replacement = new();
        internal Session? Current; internal int Captures, Checks, Generation, CapturedGeneration;
        internal Action? AfterCapture, AfterCheck;
        internal Api() => Current = Initial;
        internal void ChangeTo(Session session) { Current = session; Generation++; }
        public ICurrentMediaSession? CaptureCurrent() { Captures++; CapturedGeneration = Generation; var captured = Current; AfterCapture?.Invoke(); return captured; }
        public bool IsCurrent(ICurrentMediaSession captured) { Checks++; var same = ReferenceEquals(Current, captured) && Generation == CapturedGeneration; AfterCheck?.Invoke(); return same; }
        public void Dispose() { }
    }
    private sealed class Session : ICurrentMediaSession
    {
        internal CurrentMediaPlayback Playback = new(CurrentMediaPlaybackStatus.Other, true, true, true, true);
        internal readonly List<string> Requests = []; internal int Reads; internal bool Accepted = true;
        internal Action? AfterRead, AfterRequest;
        public CurrentMediaPlayback ReadPlayback() { Reads++; var result = Playback; AfterRead?.Invoke(); return result; }
        public bool Request(string operation) { Requests.Add(operation); AfterRequest?.Invoke(); return Accepted; }
    }
}
