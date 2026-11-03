using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

// Synthetic APIs only: these tests never instantiate the DXVA2 adapter or change any real display.
[Collection("Brightness native ownership")]
public sealed class HardwareBrightnessTests
{
    private static TaskInvocation Invocation(string target = "primary", string percent = "30", string delay = "0") => new(HardwareBrightnessCapabilities.Set, 1,
        new Dictionary<string, string> { ["target"] = target, ["percent"] = percent, ["delaySeconds"] = delay }.ToImmutableDictionary());
    [Fact]
    public async Task ActualRegistryPausedTemplateStrictImportAndSaveShareArbitrationAndEngineDelay()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<HardwareBrightnessPlugin>());
        var binding = Assert.Single(plugin.Bindings, b => b.Descriptor.Id == HardwareBrightnessCapabilities.Set).Descriptor; Assert.Equal(RetryCategory.Never, binding.Retry); Assert.Equal(TimeSpan.FromSeconds(30), binding.Timeout);
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
        all.Caps[12] = 0; all.Ranges[12] = null; all.Caps[13] = null; all.Ranges[13] = null; all.Ranges[14] = new(10, 2, 110);
        result = HardwareBrightnessOperation.Apply(all, "all", 30, default); Assert.False(result.Success); Assert.Contains("部分完成", result.Reason);
        Assert.Contains("已知不支持而跳过1块", result.Reason); Assert.Contains("未知而未写入2个目标", result.Reason); Assert.Equal(new nint[] { 11, 21 }, all.Writes.Keys);
        Assert.All(all.Groups.Values, g => Assert.Equal(1, g.Closes));
        var unsupported = new Api(); unsupported.Caps[11] = 0; unsupported.Ranges[11] = null; unsupported.Caps[21] = 0; unsupported.Ranges[21] = null;
        Assert.False(HardwareBrightnessOperation.Apply(unsupported, "all", 30, default).Success); Assert.Empty(unsupported.Writes);
        var unauthorized = new Api { Authorized = false }; Assert.False(HardwareBrightnessOperation.Apply(unauthorized, "all", 30, default).Success); Assert.Equal(0, unauthorized.Discoveries);
    }
    [Fact]
    public void KnownLiveRangeAllowsAbsoluteAndRelativeWritesWithoutCapabilitiesButUnknownOrInvalidReadsNeverWrite()
    {
        foreach (var caps in new uint?[] { null, 0 }) foreach (var relative in new[] { false, true })
        {
            var api = new Api(); api.Caps[11] = caps; api.Caps[21] = caps;
            var result = relative ? HardwareBrightnessOperation.Adjust(api, "all", "increase", 5, default) : HardwareBrightnessOperation.Apply(api, "all", 30, default);
            Assert.True(result.Success); Assert.Equal(2, api.Writes.Count); Assert.All(api.Writes.Values, value => Assert.Equal(relative ? 75u : 40u, value));
            Assert.Equal(0, api.CapabilityQueries); Assert.All(api.SetCounts.Values, count => Assert.Equal(1, count)); Assert.All(api.Groups.Values, group => Assert.Equal(1, group.Closes));
        }
        foreach (var range in new HardwareBrightnessRange?[] { null, new(3, 3, 3), new(10, 9, 110), new(10, 111, 110), new(100, 100, 10) })
        foreach (var caps in new uint?[] { null, 0, 2 })
        {
            var api = new Api(); api.Ranges[11] = range; api.Caps[11] = caps;
            var result = HardwareBrightnessOperation.Apply(api, "primary", 30, default);
            Assert.False(result.Success); Assert.Empty(api.Writes); Assert.Equal(1, api.CapabilityQueries); Assert.Equal(1, api.Groups[1].Closes);
            Assert.Contains(caps == 0 ? "已知不支持而跳过1块" : "未知而未写入1个目标", result.Reason);
        }
        var unreadable = new Api(); unreadable.ReadErrors.Add(11); unreadable.Caps[11] = 0;
        Assert.Contains("已知不支持而跳过1块", HardwareBrightnessOperation.Apply(unreadable, "primary", 30, default).Reason); Assert.Empty(unreadable.Writes);
        var unconfirmed = new Api(); unconfirmed.Caps[11] = null; unconfirmed.Readbacks[11] = null;
        Assert.False(HardwareBrightnessOperation.Apply(unconfirmed, "primary", 30, default).Success); Assert.Equal(1, unconfirmed.SetCounts[11]);
        Assert.Equal(0, unconfirmed.CapabilityQueries); Assert.Equal(1, unconfirmed.Groups[1].Closes);
    }
    [Fact]
    public void InitialCommunicationRecoveryIsBoundedAndNeverRetriesUnsupportedAccessOrSuccessfulInvalidRanges()
    {
        foreach (var error in new[] { unchecked((int)0xC0262582), unchecked((int)0xC0262583), unchecked((int)0xC0262585),
            HardwareBrightnessReadPolicy.InvalidMessageCommand, unchecked((int)0xC026258A), unchecked((int)0xC026258B) })
        foreach (var recovers in new[] { true, false })
        {
            var reads = 0; var delays = new List<double>();
            var result = HardwareBrightnessReadPolicy.Read(() => (++reads == 4 && recovers ? new HardwareBrightnessRange(0, 60, 100) : null, error),
                default, (delay, _) => delays.Add(delay.TotalMilliseconds), initial: true);
            Assert.Equal(4, reads); Assert.Equal(new double[] { 250, 750, 1500 }, delays);
            Assert.Equal(recovers, result is not null);
        }
        foreach (var error in new[] { 0, 5, 6, unchecked((int)0xC0262580), unchecked((int)0xC0262581), unchecked((int)0xC0262584) })
        {
            var reads = 0;
            Assert.Null(HardwareBrightnessReadPolicy.Read(() => { reads++; return (null, error); }, default,
                (_, _) => throw new InvalidOperationException("Permanent or unclassified failures must not wait."), initial: true));
            Assert.Equal(1, reads);
        }
        var invalid = new HardwareBrightnessRange(0, 101, 100);
        Assert.Same(invalid, HardwareBrightnessReadPolicy.Read(() => (invalid, HardwareBrightnessReadPolicy.InvalidMessageCommand), default,
            (_, _) => throw new InvalidOperationException("Successful invalid ranges must not be retried."), initial: true));
    }
    [Fact]
    public void InitialRecoveryWritesOnceAndReadbackFailureCancellationAndExhaustionNeverReplayWrites()
    {
        var communication = unchecked((int)0xC0262583);
        foreach (var mode in new[] { "recover", "exhaust", "cancel", "readback", "write" })
        {
            using var stop = new CancellationTokenSource();
            var api = new Api { InitialRecovery = true, ReadToken = stop.Token };
            for (var i = 0; i < (mode == "exhaust" ? 4 : 2); i++) api.NativeReadErrors.Enqueue(communication);
            if (mode == "cancel") api.AfterReadWait = stop.Cancel;
            if (mode == "readback") api.AfterSet = () => api.NativeReadErrors.Enqueue(communication);
            if (mode == "write") api.Refused.Add(11);
            if (mode == "cancel") Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(api, "primary", 100, stop.Token));
            else
            {
                var result = HardwareBrightnessOperation.Apply(api, "primary", 100, stop.Token);
                Assert.Equal(mode == "recover", result.Success);
                if (mode == "exhaust") Assert.Contains("本次未写入亮度", result.Reason);
            }
            Assert.Equal(mode is "exhaust" or "cancel" ? 0 : 1, api.SetCounts.GetValueOrDefault(11));
            Assert.Equal(mode == "cancel" ? 1 : mode == "exhaust" ? 3 : 2, api.ReadWaits);
            Assert.Equal(1, api.Groups[1].Closes);
        }
    }
    [Fact]
    public void ReadPolicyOnlyRequeriesObservedFramingFailuresUpToThreeTimesAndReturnsEverySuccessfulReplyImmediately()
    {
        var transient = HardwareBrightnessReadPolicy.InvalidMessageCommand;
        foreach (var errors in new[] { new[] { transient, transient, 0 }, new[] { transient, transient, transient, 0 }, new[] { 5, 0 }, new[] { unchecked((int)0xC0262584), 0 }, new[] { unchecked((int)0xC026258C), 0 }, new[] { unchecked((int)0xC026258D), 0 } })
        {
            var calls = 0; var waits = 0;
            var result = HardwareBrightnessReadPolicy.Read(() =>
            {
                var error = errors[calls++]; return (error == 0 ? new HardwareBrightnessRange(10, 70, 110) : null, error);
            }, default, (delay, _) => { waits++; Assert.Equal(TimeSpan.FromMilliseconds(150), delay); });
            var retries = errors[0] == transient; Assert.Equal(retries ? 3 : 1, calls); Assert.Equal(retries ? 2 : 0, waits);
            if (retries && errors[2] == 0) Assert.Equal(70u, result!.Current); else Assert.Null(result);
        }
        foreach (var reply in new[] { new HardwareBrightnessRange(10, 45, 110), new HardwareBrightnessRange(10, 111, 110), new HardwareBrightnessRange(0, 0, 0) })
        {
            var calls = 0;
            Assert.Same(reply, HardwareBrightnessReadPolicy.Read(() => { calls++; return (reply, 0); }, default, (_, _) => throw new InvalidOperationException("Successful replies must not wait.")));
            Assert.Equal(1, calls);
        }
        using var stop = new CancellationTokenSource(); var queried = 0; stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessReadPolicy.Read(() => { queried++; return (null, transient); }, stop.Token)); Assert.Equal(0, queried);
        using var duringWait = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessReadPolicy.Read(() => { queried++; return (null, transient); }, duringWait.Token, (_, _) => duringWait.Cancel())); Assert.Equal(1, queried);
    }
    [Fact]
    public void TransientInitialAndReadbackFailuresRequeryOnlyReadsAndCancellationKeepsOneWriteAndReleasesLeases()
    {
        foreach (var relative in new[] { false, true })
        {
            var api = new Api(); api.NativeReadErrors.Enqueue(HardwareBrightnessReadPolicy.InvalidMessageCommand);
            api.AfterSet = () => { api.NativeReadErrors.Enqueue(HardwareBrightnessReadPolicy.InvalidMessageCommand); api.NativeReadErrors.Enqueue(HardwareBrightnessReadPolicy.InvalidMessageCommand); };
            var result = relative ? HardwareBrightnessOperation.Adjust(api, "primary", "increase", 5, default) : HardwareBrightnessOperation.Apply(api, "primary", 30, default);
            Assert.True(result.Success); Assert.Equal(1, api.SetCounts[11]); Assert.Equal(3, api.ReadWaits); Assert.Equal(5, api.NativeReads); Assert.Equal(1, api.Groups[1].Closes);
        }
        using var stop = new CancellationTokenSource(); var canceled = new Api { ReadToken = stop.Token, AfterReadWait = stop.Cancel };
        canceled.AfterSet = () => canceled.NativeReadErrors.Enqueue(HardwareBrightnessReadPolicy.InvalidMessageCommand);
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(canceled, "primary", 30, stop.Token));
        Assert.Equal(1, canceled.SetCounts[11]); Assert.Equal(2, canceled.NativeReads); Assert.Equal(1, canceled.ReadWaits); Assert.Equal(1, canceled.Groups[1].Closes);
    }
    [Fact(Timeout = 10000)]
    public async Task HardwareRestoreUsesKnownRangeWithoutCapabilitiesAndKeepsOriginalRangeGuardsAndCleanup()
    {
        static Dictionary<string, string> Parameters(int value) => new() { ["percent"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture), ["target"] = "primary" };
        foreach (var caps in new uint?[] { null, 0 })
        {
            var api = new Api(); api.Caps[11] = caps; var closed = 0;
            api.NativeReadErrors.Enqueue(HardwareBrightnessReadPolicy.InvalidMessageCommand);
            api.AfterSet = () => api.NativeReadErrors.Enqueue(HardwareBrightnessReadPolicy.InvalidMessageCommand);
            var session = await BrightnessRestoreSession.CreateAsync(token =>
            {
                var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => true, token, out var unsupported);
                Assert.NotNull(target); Assert.False(unsupported);
                return new([target], 0, 0, _ => { }, () => { closed++; api.Groups[1].Dispose(); });
            }, "primary", TimeSpan.FromSeconds(10), default);
            try
            {
                Assert.True((await session.ApplyAsync(Parameters(30), default)).Owned); Assert.Equal(40u, api.Writes[11]);
                api.NativeReadErrors.Enqueue(HardwareBrightnessReadPolicy.InvalidMessageCommand);
                Assert.True((await session.ApplyAsync(Parameters(50), default)).Owned); Assert.Equal(60u, api.Writes[11]);
                Assert.True((await session.ReleaseAsync(default)).Success); Assert.Equal(70u, api.Writes[11]); Assert.Equal(3, api.SetCounts[11]);
            }
            finally { await session.DisposeAsync(); }
            Assert.Equal(0, api.CapabilityQueries); Assert.Equal(5, api.ReadWaits); Assert.Equal(1, closed); Assert.Equal(1, api.Groups[1].Closes);
        }
        foreach (var guard in new[] { "range", "unknown", "topology", "authorization", "external", "readback" })
        {
            var api = new Api(); api.Caps[11] = null; var valid = true;
            var session = await BrightnessRestoreSession.CreateAsync(token =>
            {
                var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => valid, token, out _);
                return new([target!], 0, 0, _ => { }, () => api.Groups[1].Dispose());
            }, "primary", TimeSpan.FromSeconds(10), default);
            try
            {
                Assert.True((await session.ApplyAsync(Parameters(30), default)).Owned);
                if (guard == "range") api.Readbacks[11] = new(0, 40, 110);
                else if (guard == "unknown") api.Readbacks[11] = null;
                else if (guard == "topology") valid = false;
                else if (guard == "authorization") api.Authorized = false;
                else if (guard == "external") api.Readbacks[11] = new(10, 45, 110);
                else api.AfterSet = () => api.Readbacks[11] = null;
                Assert.False((await session.ApplyAsync(Parameters(50), default)).Owned);
                await session.ReleaseAsync(default);
                Assert.Equal(guard == "readback" ? 2 : 1, api.SetCounts[11]); Assert.NotEqual(70u, api.Writes[11]);
            }
            finally { await session.DisposeAsync(); }
            Assert.Equal(0, api.CapabilityQueries); Assert.Equal(1, api.Groups[1].Closes);
        }
        var unknown = new Api(); unknown.Ranges[11] = null; unknown.Caps[11] = null;
        Assert.Null(HardwareBrightnessOperation.CaptureTarget(unknown, 11, () => true, default, out var notSupported)); Assert.False(notSupported); Assert.Empty(unknown.Writes);
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
        internal readonly HashSet<nint> Refused = [], AcquireErrors = [], ReadErrors = []; internal readonly List<nint> Acquired = [];
        internal readonly Queue<int> NativeReadErrors = [];
        internal bool Authorized = true, InitialRecovery; internal int Discoveries, CapabilityQueries, NativeReads, ReadWaits; internal Action? AfterAcquire, AfterRead, AfterSet, AfterReadWait;
        internal CancellationToken ReadToken;
        public bool IsAuthorized() => Authorized;
        public IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors() { Discoveries++; return Logical; }
        public IHardwareMonitorLease Acquire(nint monitor, int remainingCapacity)
        {
            if (AcquireErrors.Contains(monitor)) throw new IOException("synthetic unknown physical targets");
            var lease = Groups[monitor]; if (lease.Handles.Count > Math.Min(8, remainingCapacity)) throw new HardwareBrightnessLimitException();
            Acquired.Add(monitor); AfterAcquire?.Invoke(); return lease;
        }
        public uint? Capabilities(nint monitor) { CapabilityQueries++; return Caps.TryGetValue(monitor, out var caps) ? caps : 2; }
        public HardwareBrightnessRange? ReadInitial(nint monitor) => ReadCore(monitor, InitialRecovery);
        public HardwareBrightnessRange? Read(nint monitor) => ReadCore(monitor, false);
        private HardwareBrightnessRange? ReadCore(nint monitor, bool initial)
        {
            return HardwareBrightnessReadPolicy.Read(() =>
            {
                NativeReads++;
                if (ReadErrors.Contains(monitor)) throw new IOException("synthetic unknown brightness");
                var error = NativeReadErrors.Count > 0 ? NativeReadErrors.Dequeue() : 0;
                HardwareBrightnessRange? range = error != 0 ? null : Writes.TryGetValue(monitor, out var value) ? Readbacks.GetValueOrDefault(monitor, new(10, value, 110)) : Ranges.GetValueOrDefault(monitor, new(10, 70, 110));
                AfterRead?.Invoke(); return (range, error);
            }, ReadToken, (delay, _) => { if (!initial) Assert.Equal(TimeSpan.FromMilliseconds(150), delay); ReadWaits++; AfterReadWait?.Invoke(); }, initial);
        }
        public bool Set(nint monitor, uint brightness) { SetCounts[monitor] = SetCounts.GetValueOrDefault(monitor) + 1; if (Refused.Contains(monitor)) return false; Writes[monitor] = brightness; AfterSet?.Invoke(); return true; }
    }
}
