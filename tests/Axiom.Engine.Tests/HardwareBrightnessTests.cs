using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

// Synthetic APIs only: these tests never instantiate the DXVA2 adapter or change any real display.
public sealed class HardwareBrightnessTests
{
    private static TaskInvocation Invocation(string target = "primary", string percent = "30", string delay = "0") => new(HardwareBrightnessCapabilities.Set, 1,
        new Dictionary<string, string> { ["target"] = target, ["percent"] = percent, ["delaySeconds"] = delay }.ToImmutableDictionary());
    [Fact]
    public async Task ActualRegistryPausedTemplateStrictImportAndSaveShareArbitrationAndEngineDelay()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<HardwareBrightnessPlugin>());
        var binding = Assert.Single(plugin.Bindings).Descriptor; Assert.Equal(RetryCategory.Never, binding.Retry); Assert.Equal(TimeSpan.FromSeconds(10), binding.Timeout);
        Assert.DoesNotContain(plugin.Combinations, c => c.DetectionId is SessionDisplayCapabilities.State or DisplayCountCapabilities.Count);
        Assert.Contains(plugin.Combinations, c => c.DetectionId == PowerStateCapabilities.State);
        var action = new HardwareBrightnessTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("all", "100"), scope).Key);
        Assert.Equal(TimeSpan.FromSeconds(60), action.Timing(Invocation(delay: "60")).MinimumDuration);
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "shortcut-hardware-brightness");
        Assert.True(template.Draft.Paused); Assert.Equal("primary", template.Draft.Action.Parameters["target"]); Assert.Equal("30", template.Draft.Action.Parameters["percent"]);
        var detection = new TaskInvocation(HotkeyCapabilities.Pressed, 1, ImmutableDictionary<string, string>.Empty.Add("chords", "Ctrl+Alt+F9"));
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        string Proposal(TaskInvocation command) => JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id, "existing", "硬件亮度", "synthetic", detection, command));
        Assert.True(UsageAnalysis.Import(Proposal(Invocation("all")), recorder.Session, catalog).Draft!.Paused);
        foreach (var invalid in new[] { Invocation("external"), Invocation(percent: "101"), Invocation(percent: "-1"), Invocation(percent: " 30"), Invocation(delay: "86401") })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(invalid), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => HardwareBrightnessCapabilities.Validate(Invocation() with { SchemaVersion = 2 }));
        Assert.Throws<ArgumentException>(() => HardwareBrightnessCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("monitorId", "1") }));
        var compiler = new AutomationTaskCompiler(); foreach (var m in modules) { foreach (var d in m.Detections) compiler.Register(d); foreach (var a in m.Actions) compiler.Register(a); }
        foreach (var condition in new[]
        {
            new TaskInvocation(PowerSchemeStateCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("scheme", "saver")),
            new TaskInvocation(PowerSaverStateCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("state", "on"))
        })
        {
            Assert.Contains(plugin.Combinations, c => c.DetectionId == condition.CapabilityId && c.ActionId == HardwareBrightnessCapabilities.Set);
            var imported = UsageAnalysis.Import(JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id,
                "existing", "节能时硬件亮度", "synthetic", condition, Invocation())), recorder.Session, catalog).Draft!;
            Assert.True(imported.Paused); Assert.NotNull(compiler.Compile(imported, "owner", scope));
        }
        var draft = template.Draft with { Detection = detection }; var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-hardware-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(draft, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(draft.Action.Parameters, loaded.Action.Parameters); Assert.NotNull(compiler.Compile(loaded, "owner", scope));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }
    [Fact]
    public void MappingPrimaryAndAllRespectHardwareRangesKnownUnsupportedAndUnknownWithoutBatteryOrExternalAssumptions()
    {
        Assert.Equal(10u, HardwareBrightnessOperation.Map(new(10, 70, 110), 0)); Assert.Equal(40u, HardwareBrightnessOperation.Map(new(10, 70, 110), 30));
        Assert.Equal(110u, HardwareBrightnessOperation.Map(new(10, 70, 110), 100));
        Assert.Equal(2147483647u, HardwareBrightnessOperation.Map(new(0, uint.MaxValue, uint.MaxValue), 50));
        Assert.Equal(uint.MaxValue, HardwareBrightnessOperation.Map(new(1, 2, uint.MaxValue), 100));
        foreach (var range in new HardwareBrightnessRange?[] { null, new(3, 3, 3), new(10, 5, 100), new(10, 101, 100), new(100, 100, 10) }) Assert.Null(HardwareBrightnessOperation.Map(range, 30));
        var primary = new Api(); primary.Groups[1] = new([11, 12]); primary.Groups[2] = new([21]);
        var result = HardwareBrightnessOperation.Apply(primary, "primary", 30, default); Assert.True(result.Success); Assert.Equal(new nint[] { 1 }, primary.Acquired);
        Assert.Equal(40u, primary.Writes[11]); Assert.Equal(40u, primary.Writes[12]); Assert.False(primary.Writes.ContainsKey(21)); Assert.Equal(1, primary.Groups[1].Closes); Assert.Equal(0, primary.Groups[2].Closes);
        var all = new Api(); all.Groups[1] = new([11, 12, 13, 14]); all.Groups[2] = new([21]);
        all.Caps[12] = 0; all.Caps[13] = null; all.Ranges[14] = new(10, 2, 110);
        result = HardwareBrightnessOperation.Apply(all, "all", 30, default); Assert.False(result.Success); Assert.Contains("部分完成", result.Reason);
        Assert.Contains("已知不支持而跳过1块", result.Reason); Assert.Contains("未知而未写入2个目标", result.Reason); Assert.Equal(new nint[] { 11, 21 }, all.Writes.Keys);
        Assert.All(all.Groups.Values, g => Assert.Equal(1, g.Closes));
        var unsupported = new Api(); unsupported.Caps[11] = 0; unsupported.Caps[21] = 0;
        Assert.False(HardwareBrightnessOperation.Apply(unsupported, "all", 30, default).Success); Assert.Empty(unsupported.Writes);
        var unauthorized = new Api { Authorized = false }; Assert.False(HardwareBrightnessOperation.Apply(unauthorized, "all", 30, default).Success); Assert.Equal(0, unauthorized.Discoveries);
    }
    [Fact]
    public void PartialFailuresReadbackMismatchAndBoundsNeverRetryOrClaimAllTargetsSucceededAndReleaseEveryLease()
    {
        var partial = new Api(); partial.Groups[1] = new([11, 12, 13, 14]); partial.Refused.Add(12); partial.Readbacks[13] = new(10, 39, 110); partial.Readbacks[14] = new(0, 40, 110);
        var result = HardwareBrightnessOperation.Apply(partial, "all", 30, default); Assert.False(result.Success); Assert.Contains("已确认2块", result.Reason); Assert.Contains("未确认3块", result.Reason);
        Assert.All(partial.Writes.Keys, h => Assert.Equal(1, partial.SetCounts[h])); Assert.All(partial.Groups.Values, g => Assert.Equal(1, g.Closes));
        var unknownGroup = new Api(); unknownGroup.AcquireErrors.Add(2); result = HardwareBrightnessOperation.Apply(unknownGroup, "all", 30, default);
        Assert.False(result.Success); Assert.Single(unknownGroup.Writes); Assert.Contains("部分完成", result.Reason); Assert.Equal(1, unknownGroup.Groups[1].Closes);
        foreach (var invalid in new[] { new HardwareLogicalMonitor[0], Enumerable.Range(1, 65).Select(i => new HardwareLogicalMonitor(i, i == 1)).ToArray(), new[] { new HardwareLogicalMonitor(1, false) }, new[] { new HardwareLogicalMonitor(1, true), new HardwareLogicalMonitor(1, false) } })
        { var api = new Api { Logical = invalid }; Assert.False(HardwareBrightnessOperation.Apply(api, "all", 30, default).Success); Assert.Empty(api.Acquired); Assert.Empty(api.Writes); }
        var tooMany = new Api { Logical = Enumerable.Range(1, 9).Select(i => new HardwareLogicalMonitor(i, i == 1)).ToArray() };
        tooMany.Groups.Clear(); for (var i = 1; i <= 9; i++) tooMany.Groups[i] = new(Enumerable.Range(i * 10, 8).Select(n => (nint)n).ToArray());
        Assert.False(HardwareBrightnessOperation.Apply(tooMany, "all", 30, default).Success); Assert.Empty(tooMany.Writes);
        Assert.Equal(8, tooMany.Acquired.Count); Assert.All(tooMany.Acquired, h => Assert.Equal(1, tooMany.Groups[h].Closes));
        var cleanup = new Api(); cleanup.Groups[1].FailClose = true; result = HardwareBrightnessOperation.Apply(cleanup, "all", 30, default);
        Assert.False(result.Success); Assert.Contains("句柄释放未确认", result.Reason); Assert.Equal(1, cleanup.Groups[2].Closes);
    }
    [Fact]
    public void CancellationAndUnknownAuthorizationAfterQueryStopUnsentWritesButKeepAlreadyChangedValues()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Api();
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(untouched, "all", 30, before.Token)); Assert.Equal(0, untouched.Discoveries);
        using var acquire = new CancellationTokenSource(); var acquired = new Api { AfterAcquire = acquire.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(acquired, "all", 30, acquire.Token)); Assert.Equal(1, acquired.Groups[1].Closes); Assert.Empty(acquired.Writes);
        using var reading = new CancellationTokenSource(); var queried = new Api { AfterRead = reading.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(queried, "all", 30, reading.Token)); Assert.Empty(queried.Writes); Assert.All(queried.Groups.Values, g => Assert.Equal(1, g.Closes));
        using var writing = new CancellationTokenSource(); var changed = new Api { AfterSet = writing.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(changed, "all", 30, writing.Token)); Assert.Single(changed.Writes); Assert.Equal(40u, changed.Writes[11]); Assert.All(changed.Groups.Values, g => Assert.Equal(1, g.Closes));
        var deniedAfterQuery = new Api(); deniedAfterQuery.AfterRead = () => deniedAfterQuery.Authorized = false;
        var result = HardwareBrightnessOperation.Apply(deniedAfterQuery, "all", 30, default); Assert.False(result.Success); Assert.Empty(deniedAfterQuery.Writes); Assert.Contains("未写入2个目标", result.Reason);
    }
    [Fact(Timeout = 15000)]
    public async Task CanceledOrTimedOutQueryAndWriteHoldNativeAdmissionAndEveryHandleUntilActualReturn()
    {
        foreach (var timeout in new[] { false, true }) foreach (var query in new[] { false, true })
        {
            var commands = new BoundedNativeCommand("硬件屏幕亮度", TimeSpan.FromSeconds(10)); var time = new ManualTimeProvider();
            var timerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); time.TimerCreated = _ => timerReady.TrySetResult();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
            var api = new Api(); Action block = () => { entered.TrySetResult(); release.Wait(); };
            if (query) api.AfterRead = block; else api.AfterSet = block;
            var request = commands.RunAsync(token => HardwareBrightnessOperation.Apply(api, "all", 30, token), stop.Token, time);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await timerReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (timeout) { time.Advance(TimeSpan.FromSeconds(10)); Assert.False((await request).Success); }
                else { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); }
                Assert.False(commands.DrainAsync().IsCompleted); Assert.All(api.Groups.Values, g => Assert.Equal(0, g.Closes));
                var overlap = new Api(); Assert.False((await commands.RunAsync(token => HardwareBrightnessOperation.Apply(overlap, "all", 30, token), default)).Success); Assert.Equal(0, overlap.Discoveries);
            }
            finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            Assert.All(api.Groups.Values, g => Assert.Equal(1, g.Closes)); Assert.Equal(query ? 0 : 1, api.Writes.Count); Assert.False(api.Writes.ContainsKey(21));
        }
    }
    private sealed class Lease(nint[] handles) : IHardwareMonitorLease
    {
        public IReadOnlyList<nint> Handles => handles;
        internal int Closes; internal bool FailClose;
        public void Dispose() { Closes++; if (FailClose) throw new IOException("synthetic cleanup error"); }
    }
    private sealed class Api : IHardwareBrightnessApi
    {
        internal IReadOnlyList<HardwareLogicalMonitor> Logical = [new(1, true), new(2, false)];
        internal readonly Dictionary<nint, Lease> Groups = new() { [1] = new([11]), [2] = new([21]) };
        internal readonly Dictionary<nint, uint?> Caps = []; internal readonly Dictionary<nint, HardwareBrightnessRange?> Ranges = [], Readbacks = [];
        internal readonly Dictionary<nint, uint> Writes = []; internal readonly Dictionary<nint, int> SetCounts = [];
        internal readonly HashSet<nint> Refused = [], AcquireErrors = []; internal readonly List<nint> Acquired = [];
        internal bool Authorized = true; internal int Discoveries; internal Action? AfterAcquire, AfterRead, AfterSet;
        public bool IsAuthorized() => Authorized;
        public IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors() { Discoveries++; return Logical; }
        public IHardwareMonitorLease Acquire(nint monitor, int remainingCapacity)
        {
            if (AcquireErrors.Contains(monitor)) throw new IOException("synthetic unknown physical targets");
            var lease = Groups[monitor]; if (lease.Handles.Count > Math.Min(8, remainingCapacity)) throw new HardwareBrightnessLimitException();
            Acquired.Add(monitor); AfterAcquire?.Invoke(); return lease;
        }
        public uint? Capabilities(nint monitor) => Caps.TryGetValue(monitor, out var caps) ? caps : 2;
        public HardwareBrightnessRange? Read(nint monitor)
        {
            HardwareBrightnessRange? range = Writes.TryGetValue(monitor, out var value) ? Readbacks.GetValueOrDefault(monitor, new(10, value, 110)) : Ranges.GetValueOrDefault(monitor, new(10, 70, 110));
            AfterRead?.Invoke(); return range;
        }
        public bool Set(nint monitor, uint brightness) { SetCounts[monitor] = SetCounts.GetValueOrDefault(monitor) + 1; if (Refused.Contains(monitor)) return false; Writes[monitor] = brightness; AfterSet?.Invoke(); return true; }
    }
}
