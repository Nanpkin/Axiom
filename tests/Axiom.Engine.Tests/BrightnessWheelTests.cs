using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.ServiceHost;

namespace Axiom.Engine.Tests;

[Collection("Brightness native ownership")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BrightnessWheelTests
{
    [Fact]
    public async Task ToolAndProviderEnvelopesRejectUnrelatedFieldsAndValidateChordsAndBounds()
    {
        var now = DateTimeOffset.UtcNow; var settings = new BrightnessWheelSettings(Enabled: true);
        var save = new AutomationMessage(1, "save-brightness-wheel", "tool", now.AddSeconds(10), WheelSettings: settings);
        foreach (var modifiers in new[] { ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutModifiers.Control | ShortcutModifiers.Shift, ShortcutModifiers.Windows })
        foreach (var key in new[] { "", "B" }) BrightnessWheelTool.Validate(settings with { Modifiers = modifiers, Key = key });
        Assert.Equal("Ctrl + Shift + B + 滚轮", BrightnessWheelTool.Describe(settings with { Modifiers = ShortcutModifiers.Control | ShortcutModifiers.Shift, Key = "B" }));
        AutomationProtocol.ValidateRequest(save, now);
        AutomationProtocol.ValidateRequest(save with { Type = "read-brightness-wheel", WheelSettings = null }, now);
        AutomationProtocol.ValidateRequest(save with { Type = "resume-brightness-wheel", WheelSettings = null, ExpectedVersion = 1 }, now);
        foreach (var invalid in new[] { settings with { Step = 0 }, settings with { Step = 21 }, settings with { SmoothMilliseconds = 49 }, settings with { SmoothMilliseconds = 1001 },
            settings with { WritesPerSecond = 0 }, settings with { WritesPerSecond = 61 }, settings with { Modifiers = ShortcutModifiers.None }, settings with { Key = "Unknown" }, settings with { Target = "device-id" },
            settings with { PreferenceMinutes = -1 }, settings with { PreferenceMinutes = 241 } })
            Assert.Throws<ArgumentException>(() => BrightnessWheelTool.Validate(invalid));
        foreach (var invalid in new[] { save with { Type = "read", WheelSettings = settings }, save with { ExpectedVersion = 1 }, save with { Bindings = [] },
            save with { TraySettings = new(0, false, "startup", []) }, save with { Wheel = new(settings, "forged") }, save with { StartupEnabled = true },
            save with { Type = "resume-brightness-wheel", WheelSettings = null }, save with { Type = "save-brightness-wheel", WheelSettings = null } })
            Assert.ThrowsAny<Exception>(() => AutomationProtocol.ValidateRequest(invalid, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(save, now, shortcutAgent: true));
        var observe = new ProviderMessage(1, "observe-brightness-wheel", Guid.NewGuid().ToString("N"), now.AddSeconds(10), WheelSettings: settings);
        ProviderProtocol.Validate(observe, now);
        foreach (var invalid in new[] { observe with { CommandId = Guid.NewGuid() }, observe with { WheelSteps = 1 }, observe with { Invocation = BrightnessWheelTool.Gesture(Guid.NewGuid().ToString("N"), 0) },
            observe with { Type = "invoke" }, observe with { WheelSettings = settings with { Enabled = false } }, observe with { Resources = [] } })
            Assert.ThrowsAny<Exception>(() => ProviderProtocol.Validate(invalid, now));
        foreach (var type in new[] { "abandon-brightness", "resume-brightness" })
        {
            var message = observe with { Type = type, WheelSettings = null }; ProviderProtocol.Validate(message, now);
            Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(message with { CommandId = Guid.NewGuid() }, now));
        }
        var gesture = BrightnessWheelTool.Gesture(Guid.NewGuid().ToString("N"), -7);
        Assert.Equal(-7, BrightnessWheelTool.ValidateGesture(gesture).FromSteps);
        Assert.Throws<ArgumentException>(() => BrightnessWheelTool.ValidateGesture(gesture with { Parameters = gesture.Parameters.Add("percent", "100") }));
        using var wire = new MemoryStream(); await AutomationProtocol.WriteAsync(wire, save, default); wire.Position = 0;
        Assert.Equal(settings, (await AutomationProtocol.ReadAsync(wire, default)).WheelSettings);
        var highResolution = new BrightnessWheelDelta();
        Assert.Equal(0, highResolution.Add(30)); Assert.Equal(0, highResolution.Add(60)); Assert.Equal(1, highResolution.Add(30));
        Assert.Equal(0, highResolution.Add(-60)); Assert.Equal(-2, highResolution.Add(-180));
        highResolution.Add(30); highResolution.Reset(); Assert.Equal(0, highResolution.Add(90));
    }

    [Fact(Timeout = 10000)]
    public async Task ImmediateInputRespondsToOneSmallMessageAndKeepsPrecisionModeAvailable()
    {
        await Task.Yield();
        var old = JsonSerializer.Deserialize<BrightnessWheelSettings>("{\"Version\":1}")!;
        Assert.True(old.ImmediateResponse);
        Assert.True(old.CoordinateAutomatic); Assert.Equal(30, old.PreferenceMinutes);
        var immediate = new BrightnessWheelDelta(old.ImmediateResponse);
        foreach (var delta in new[] { 1, 30, 60, 90, 120, -1, -30, -60, -90, -120 })
            Assert.Equal(Math.Sign(delta), immediate.Add(delta));
        Assert.Equal(0, immediate.Add(0)); Assert.Equal(3, immediate.Add(360)); Assert.Equal(-2, immediate.Add(-240));
        var precision = new BrightnessWheelDelta(false);
        Assert.Equal(0, precision.Add(60)); Assert.Equal(1, precision.Add(60));
        var time = new ManualTimeProvider(); uint current = 80;
        var target = new BrightnessRestoreTarget(80, p => (uint)p, () => current, value => { current = value; return true; }, hardwareBurst: true);
        using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
        var oneSmallInput = immediate.Add(-30);
        var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, () => oneSmallInput, default, time, (delay, _) => time.Advance(delay));
        Assert.True(result.Success); Assert.Equal(78u, current); Assert.Equal(-1, result.ConsumedSteps);
    }

    [Fact(Timeout = 10000)]
    public async Task CoordinatedWheelRetainsBaselineAndReusesOnlyMatchingUnexpiredPreference()
    {
        var time = new ManualTimeProvider(); uint current = 100; var context = Guid.NewGuid().ToString("N");
        var parameters = new Dictionary<string, string> { ["percent"] = "50", ["target"] = "primary", ["context"] = context };
        async Task<IRestorableSettingSession> Open() => await BrightnessRestoreSession.CreateAsync(_ =>
            new([new(current, p => (uint)p, () => current, value => { current = value; return true; }, hardwareBurst: true)], 0, 0, _ => { }, () => { }),
            "primary", TimeSpan.FromSeconds(5), default, time, (delay, _) => time.Advance(delay));
        await using (var session = await Open())
        {
            Assert.True((await session.ApplyAsync(parameters, default)).Success); Assert.Equal(50u, current);
            var wheel = await BrightnessNativeOwnership.Coordinate(new("primary", 5, 150, 18, 0, () => 3, 30, default))!;
            Assert.True(wheel.Success); Assert.Equal(65u, current);
            Assert.True((await session.ApplyAsync(parameters, default)).Success); Assert.Equal(65u, current);
            // Expiry never fights a user who remains in this active scene.
            time.Advance(TimeSpan.FromMinutes(31));
            Assert.True((await session.ApplyAsync(parameters, default)).Success); Assert.Equal(65u, current);
            Assert.True((await session.ReleaseAsync(default)).Success); Assert.Equal(100u, current);
        }
        await using (var expired = await Open())
        {
            Assert.True((await expired.ApplyAsync(parameters, default)).Success); Assert.Equal(50u, current);
            Assert.True((await BrightnessNativeOwnership.Coordinate(new("primary", 5, 150, 18, 0, () => 3, 30, default))!).Success);
            Assert.True((await expired.ReleaseAsync(default)).Success); Assert.Equal(100u, current);
        }
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await using var repeated = await Open();
            Assert.True((await repeated.ApplyAsync(parameters, default)).Success); Assert.Equal(65u, current);
            Assert.True((await repeated.ReleaseAsync(default)).Success); Assert.Equal(100u, current);
        }
        // A different action or ordinary manual baseline does not inherit this correction.
        current = 80;
        await using (var ordinary = await Open())
        {
            Assert.True((await ordinary.ApplyAsync(parameters, default)).Success); Assert.Equal(50u, current);
            Assert.True((await ordinary.ReleaseAsync(default)).Success); Assert.Equal(80u, current);
        }
        current = 100;
        await using (var changed = await Open())
        {
            Assert.True((await changed.ApplyAsync(new Dictionary<string, string>(parameters) { ["percent"] = "40" }, default)).Success);
            Assert.Equal(40u, current); Assert.True((await changed.ReleaseAsync(default)).Success); Assert.Equal(100u, current);
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedWheelRefusesOtherTargetsAndNeverOverwritesExternalBrightness(bool external)
    {
        var time = new ManualTimeProvider(); uint current = 100;
        var parameters = new Dictionary<string, string> { ["percent"] = "50", ["target"] = "primary", ["context"] = Guid.NewGuid().ToString("N") };
        await using var session = await BrightnessRestoreSession.CreateAsync(_ => new([new(100, p => (uint)p, () => current,
            value => { current = value; return true; }, hardwareBurst: true)], 0, 0, _ => { }, () => { }),
            "primary", TimeSpan.FromSeconds(5), default, time, (delay, _) => time.Advance(delay));
        Assert.True((await session.ApplyAsync(parameters, default)).Success);
        var mismatch = await BrightnessNativeOwnership.Coordinate(new("all", 5, 150, 18, 0, () => 1, 0, default))!;
        Assert.False(mismatch.Success); Assert.Equal(50u, current);
        if (external) current = 72;
        var result = await BrightnessNativeOwnership.Coordinate(new("primary", 5, 150, 18, 0, () => 1, 0, default))!;
        Assert.Equal(!external, result.Success);
        await session.ReleaseAsync(default);
        Assert.Equal(external ? 72u : 100u, current);
    }

    [Fact(Timeout = 10000)]
    public async Task SceneExitCancelsInFlightManualInputAndRestoresOnOriginalWorker()
    {
        var time = new ManualTimeProvider(); uint current = 100; var owner = 0;
        using var finish = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await BrightnessRestoreSession.CreateAsync(_ =>
        {
            owner = Environment.CurrentManagedThreadId;
            return new([new(100, p => (uint)p, () => { Assert.Equal(owner, Environment.CurrentManagedThreadId); return current; }, value =>
            {
                Assert.Equal(owner, Environment.CurrentManagedThreadId); current = value;
                if (value is > 50 and < 100) { entered.TrySetResult(); finish.Wait(); }
                return true;
            }, hardwareBurst: true)], 0, 0, _ => { }, () => { });
        }, "primary", TimeSpan.FromSeconds(5), default, time, (delay, token) => { time.Advance(delay); token.ThrowIfCancellationRequested(); });
        try
        {
            Assert.True((await session.ApplyAsync(new Dictionary<string, string> { ["percent"] = "50", ["target"] = "primary" }, default)).Success);
            var gesture = BrightnessNativeOwnership.Coordinate(new("primary", 5, 500, 18, 0, () => 3, 0, default))!;
            await entered.Task;
            var release = session.ReleaseAsync(default);
            Assert.False(release.IsCompleted); Assert.Null(BrightnessNativeOwnership.TryAcquire());
            var duringRelease = await BrightnessNativeOwnership.Coordinate(new("primary", 5, 500, 18, 3, () => 4, 0, default))!;
            Assert.True(duringRelease.Success); Assert.Equal(4, duringRelease.ConsumedSteps); Assert.Contains("本次输入未修改", duringRelease.Reason);
            finish.Set();
            var stopped = await gesture;
            Assert.True(stopped.Success); Assert.Contains("场景已结束", stopped.Reason); Assert.Equal(3, stopped.ConsumedSteps);
            Assert.True((await release).Success); Assert.Equal(100u, current);
        }
        finally { finish.Set(); }
    }

    [Fact(Timeout = 10000)]
    public async Task ExplicitHandoffCancelsPendingFadeWithoutRestoringAndRetainsAdmissionUntilNativeCleanup()
    {
        uint current = 73; var writes = new List<uint>(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim(); var closed = false; var ownerThread = 0;
        var session = await BrightnessRestoreSession.CreateAsync(_ =>
        {
            ownerThread = Environment.CurrentManagedThreadId;
            return new([new(73, p => (uint)p, () => current, value =>
            { current = value; writes.Add(value); entered.TrySetResult(); finish.Wait(); return true; }, hardwareBurst: true)], 0, 0, _ => { },
                () => { Assert.Equal(ownerThread, Environment.CurrentManagedThreadId); closed = true; });
        }, null, TimeSpan.FromSeconds(5), default);
        try
        {
            var apply = session.ApplyAsync(new Dictionary<string, string> { ["percent"] = "40" }, default);
            await entered.Task;
            var drained = BrightnessNativeOwnership.BeginManual();
            Assert.False(drained.IsCompleted); Assert.Null(BrightnessNativeOwnership.TryAcquire()); Assert.Null(BrightnessNativeOwnership.TryAcquire(manualGesture: true));
            finish.Set(); await apply; await drained;
            Assert.True(closed); Assert.Equal(new uint[] { 40 }, writes); Assert.Equal(40u, current);
            Assert.True((await session.ReleaseAsync(default)).Success); Assert.Equal(new uint[] { 40 }, writes);
            Assert.Null(BrightnessNativeOwnership.TryAcquire());
            using var manual = BrightnessNativeOwnership.TryAcquire(manualGesture: true); Assert.NotNull(manual);
            Assert.Throws<InvalidOperationException>(() => BrightnessNativeOwnership.EndManual());
        }
        finally { finish.Set(); await session.DisposeAsync(); BrightnessNativeOwnership.EndManual(); }
        using var ordinary = BrightnessNativeOwnership.TryAcquire(); Assert.NotNull(ordinary);
    }

    [Fact]
    public void EndpointConfirmsPriorSegmentBeforeSetAndRechecksInputAfterSlowRead()
    {
        foreach (var retarget in new[] { false, true })
        {
            var time = new ManualTimeProvider(); uint current = 97; long latest = -2;
            var unconfirmed = false; var reads = 0; var writes = new List<uint>();
            var target = new BrightnessRestoreTarget(97, p => (uint)p, () =>
            {
                reads++; unconfirmed = false;
                if (reads == 2)
                {
                    time.Advance(TimeSpan.FromMilliseconds(250));
                    if (retarget) latest = 2;
                }
                return current;
            }, value =>
            {
                if (value is 93 or 100) Assert.False(unconfirmed);
                writes.Add(value); current = value; unconfirmed = true;
                time.Advance(TimeSpan.FromMilliseconds(55)); return true;
            }, hardwareBurst: true);
            using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
            var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, () => latest, default, time,
                (delay, _) => time.Advance(delay));
            Assert.True(result.Success); Assert.Equal(retarget ? 100u : 93u, current);
            Assert.Equal(latest, result.ConsumedSteps); Assert.False(target.Transaction.HasPendingFadeWrite);
            if (retarget) Assert.DoesNotContain(93u, writes);
            Assert.Single(writes, value => value == current);
            Assert.True(reads >= 3);
        }
    }

    [Fact]
    public void CoalescedIntermediateWritesRebaseBeforeEndpointButNeverConfirmAnUnreachedEndpoint()
    {
        foreach (var dropEndpoint in new[] { false, true })
        {
            var time = new ManualTimeProvider(); uint current = 97; var awaitingRead = false; var coalesced = false;
            var writes = new List<uint>();
            var target = new BrightnessRestoreTarget(97, p => (uint)p,
                () => { awaitingRead = false; return current; }, value =>
                {
                    writes.Add(value);
                    if (awaitingRead) coalesced = true;
                    if (!awaitingRead && !(dropEndpoint && value == 77)) current = value;
                    awaitingRead = true; time.Advance(TimeSpan.FromMilliseconds(55)); return true;
                }, hardwareBurst: true);
            using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
            var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 500, 60, 0, () => -10, default, time,
                (delay, _) => time.Advance(delay));
            Assert.True(coalesced);
            Assert.Equal(!dropEndpoint, result.Success);
            if (!dropEndpoint) Assert.Equal(77u, current);
            else Assert.NotEqual(77u, current);
            Assert.Single(writes, value => value == 77);
        }
    }

    [Fact]
    public void ContinuousInputRetargetsOneCaptureClampsBoundsAndConfirmsLatestEndpoint()
    {
        var time = new ManualTimeProvider(); uint current = 73; long latest = -1;
        var values = new List<uint>(); var at = new List<TimeSpan>(); var reads = 0;
        var target = new BrightnessRestoreTarget(73, p => (uint)p, () => { reads++; return current; }, value =>
        { values.Add(value); at.Add(time.GetElapsedTime(0)); current = value; time.Advance(TimeSpan.FromMilliseconds(55)); return true; }, hardwareBurst: true);
        using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
        var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, () => latest, default, time, (delay, _) =>
        {
            time.Advance(delay);
            if (time.GetElapsedTime(0) >= TimeSpan.FromMilliseconds(100)) latest = -5;
        });
        Assert.True(result.Success); Assert.Equal(-5, result.ConsumedSteps); Assert.Equal(63u, current); Assert.Equal(63u, target.Transaction.Confirmed);
        Assert.False(target.Transaction.HasPendingFadeWrite); Assert.Equal(values.Distinct(), values); Assert.Equal(values.OrderDescending(), values);
        Assert.True(at[^1] - at[^2] >= TimeSpan.FromMilliseconds(100)); Assert.InRange(reads, 2, 3);
        Assert.True(time.GetElapsedTime(0) < TimeSpan.FromSeconds(1));
        foreach (var steps in new[] { -100L, 100L })
        {
            current = 73; var bounded = new BrightnessRestoreTarget(73, p => (uint)p, () => current, value => { current = value; return true; }, hardwareBurst: true);
            using var bounds = new BrightnessRestoreCapture([bounded], 0, 0, _ => { }, () => { });
            var boundedResult = WindowsBrightnessWheelGesture.RunCaptured(bounds, 20, 50, 60, 0, () => steps, default, time, (delay, _) => time.Advance(delay));
            Assert.True(boundedResult.Success); Assert.Equal(steps < 0 ? 0u : 100u, current);
        }
    }

    [Fact]
    public void ReversalWaits100msBeforeStrictFenceAndUsesLatestInputWithoutRestoringBaseline()
    {
        var time = new ManualTimeProvider(); uint current = 73; long latest = -10;
        var writes = new List<(TimeSpan At, uint Value)>(); var reads = new List<(TimeSpan At, uint Value)>(); var flipped = false;
        var target = new BrightnessRestoreTarget(73, p => (uint)p, () =>
        {
            var value = writes.Count == 1 && time.GetElapsedTime(0) - writes[0].At < TimeSpan.FromMilliseconds(100) ? 73u : current;
            reads.Add((time.GetElapsedTime(0), value)); return value;
        }, value => { writes.Add((time.GetElapsedTime(0), value)); current = value; time.Advance(TimeSpan.FromMilliseconds(55)); return true; }, hardwareBurst: true);
        using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
        var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, () => latest, default, time, (delay, _) =>
        {
            time.Advance(delay);
            if (writes.Count == 1 && !flipped) { latest = 5; flipped = true; }
        });
        Assert.True(result.Success); Assert.Equal(5, result.ConsumedSteps); Assert.Equal(83u, current);
        Assert.True(reads[1].At - writes[0].At >= TimeSpan.FromMilliseconds(100)); Assert.Equal(writes[0].Value, reads[1].Value);
        Assert.True(reads[1].At < writes[1].At); Assert.Equal(73u, target.Baseline);
        Assert.False(target.Transaction.HasPendingFadeWrite); Assert.False(target.Transaction.UnconfirmedFailure);
    }

    [Fact]
    public void EndpointFenceRechecksEarlyWakeAndFreezesCandidateWhenClockAdvancesBeforeWrite()
    {
        foreach (var preempt in new[] { false, true }) foreach (var cancel in new[] { false, true })
        {
            var time = new ManualTimeProvider(); using var stop = new CancellationTokenSource(); uint current = 96;
            var reads = 0; var waits = 0; var observationsAfterFirstWrite = 0; var preempted = false;
            var writes = new List<(TimeSpan At, uint Value)>();
            var target = new BrightnessRestoreTarget(96, p => (uint)p, () =>
            {
                if (++reads == 1) time.Advance(TimeSpan.FromMilliseconds(preempt ? 22 : 55.4));
                return current;
            }, value =>
            {
                var at = time.GetElapsedTime(0);
                // Model the observed device: Set accepts every value, but a final write spaced
                // below 100ms can leave the previous accepted brightness physically unchanged.
                if (value != 100 || writes.Count == 0 || at - writes[^1].At >= TimeSpan.FromMilliseconds(100)) current = value;
                writes.Add((at, value)); time.Advance(TimeSpan.FromMilliseconds(55)); return true;
            }, hardwareBurst: true);
            using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
            long Latest()
            {
                if (preempt && writes.Count == 1 && ++observationsAfterFirstWrite == 3)
                { preempted = true; time.Advance(TimeSpan.FromMilliseconds(20)); }
                return 2;
            }
            void Wait(TimeSpan delay, CancellationToken token)
            {
                waits++;
                // Task.Delay can return just before a fractional-millisecond deadline.
                time.Advance(delay - TimeSpan.FromMilliseconds(0.4));
                if (cancel && writes.Count > 0 && delay > TimeSpan.FromMilliseconds(16)) stop.Cancel();
                token.ThrowIfCancellationRequested();
            }
            if (cancel)
            {
                Assert.ThrowsAny<OperationCanceledException>(() => WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, Latest, stop.Token, time, Wait));
                Assert.DoesNotContain(writes, write => write.Value == 100); Assert.True(target.Transaction.HasPendingFadeWrite);
                target.Transaction.Relinquish(); Assert.False(target.Transaction.TryCompleteConfirmedRelease());
            }
            else
            {
                var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, Latest, default, time, Wait);
                Assert.True(result.Success); Assert.Equal(100u, current); Assert.Equal(100u, target.Transaction.Confirmed);
                Assert.False(target.Transaction.HasPendingFadeWrite); Assert.Single(writes, write => write.Value == 100);
                Assert.True(writes[^1].At - writes[^2].At >= TimeSpan.FromMilliseconds(100));
                if (preempt) { Assert.True(preempted); Assert.Equal(99u, writes[1].Value); }
            }
            Assert.InRange(waits, 1, 30); Assert.True(time.GetElapsedTime(0) < TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void GestureUnknownWriteFailureOrCancellationStopsWithoutRetryOrBaselineRestoration()
    {
        foreach (var failure in new[] { "read", "write", "cancel" })
        {
            var time = new ManualTimeProvider(); using var stop = new CancellationTokenSource(); uint current = 73; var writes = new List<uint>(); var reads = 0;
            var target = new BrightnessRestoreTarget(73, p => (uint)p, () => ++reads > 1 && failure == "read" ? null : current, value =>
            { writes.Add(value); current = value; if (failure == "cancel") stop.Cancel(); return failure != "write"; }, hardwareBurst: true);
            using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
            if (failure == "cancel")
            {
                BrightnessWheelGestureResult? cancelled = null;
                var error = Record.Exception(() => cancelled = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 50, 18, 0, () => -1, stop.Token, time,
                    (delay, token) => { time.Advance(delay); token.ThrowIfCancellationRequested(); }));
                if (error is null) Assert.False(cancelled!.Success); else Assert.IsAssignableFrom<OperationCanceledException>(error);
            }
            else Assert.False(WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 50, 18, 0, () => -1, default, time, (delay, _) => time.Advance(delay)).Success);
            var before = writes.Count; target.Transaction.Relinquish(); target.Transaction.Release(default);
            Assert.Equal(before, writes.Count); Assert.DoesNotContain(73u, writes); Assert.False(target.Transaction.TryCompleteConfirmedRelease());
            if (failure is "cancel" or "write") Assert.Single(writes);
        }
    }

    [Theory(Timeout = 15000)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task HostWaitsForMarkedAndCompleteAndDisableResumesOnlyAfterNativeCleanupConfirmed(bool resumeSucceeds, bool coordinated)
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-wheel-pipe-synthetic-" + Guid.NewGuid().ToString("N"), "tasks.db");
        var sid = "wheel-test-" + Guid.NewGuid().ToString("N");
        var owner = WindowsIdentity.GetCurrent().User!.Value;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wheel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new List<Task>();
        async Task Handle(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                var request = await ProviderProtocol.ReadAsync(pipe, limit.Token);
                ProviderProtocol.Validate(request, DateTimeOffset.UtcNow);
                if (request.Type == "observe")
                {
                    await ProviderProtocol.WriteAsync(pipe, new(1, "snapshot", request.RequestId, Sequence: 0, Samples: []), limit.Token);
                    try { await ProviderProtocol.ReadAsync(pipe, serverStop.Token); }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { }
                }
                else if (request.Type == "observe-brightness-wheel")
                {
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-wheel-state", request.RequestId, Sequence: 0, Status: OutcomeStatus.Succeeded, WheelSteps: 0), limit.Token);
                    ready.TrySetResult(); await wheel.Task.WaitAsync(limit.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-wheel-state", request.RequestId, Sequence: 1, Status: OutcomeStatus.Succeeded, WheelSteps: -2), limit.Token);
                    try { await ProviderProtocol.ReadAsync(pipe, limit.Token); } catch (IOException) { disconnected.TrySetResult(); }
                }
                else if (request.Type == "abandon-brightness")
                {
                    Assert.False(coordinated);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-abandon-marked", request.RequestId, Status: OutcomeStatus.Succeeded), limit.Token);
                    marked.TrySetResult(); await complete.Task.WaitAsync(limit.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-abandon-complete", request.RequestId, Status: OutcomeStatus.Succeeded), limit.Token);
                }
                else if (request.Type == "resume-brightness")
                {
                    Assert.False(coordinated);
                    await disconnected.Task.WaitAsync(limit.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-resumed", request.RequestId,
                        Status: resumeSucceeds ? OutcomeStatus.Succeeded : OutcomeStatus.Failed), limit.Token);
                }
                else
                {
                    if (!coordinated) Assert.True(complete.Task.IsCompletedSuccessfully); Assert.Equal("invoke", request.Type);
                    Assert.Equal(BrightnessWheelTool.Command, request.Invocation!.CapabilityId); Assert.NotNull(request.CommandId);
                    Assert.Equal(0, BrightnessWheelTool.ValidateGesture(request.Invocation).FromSteps);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId,
                        Status: OutcomeStatus.Succeeded, Reason: "synthetic strict endpoint confirmed", WheelSteps: -2), limit.Token);
                    invoked.TrySetResult();
                }
            }
        }
        async Task Accept()
        {
            while (!serverStop.IsCancellationRequested)
            {
                var pipe = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), owner);
                try { await pipe.WaitForConnectionAsync(serverStop.Token); }
                catch (OperationCanceledException) { pipe.Dispose(); return; }
                var client = Handle(pipe); clients.Add(client);
                _ = client.ContinueWith(failed => invoked.TrySetException(failed.Exception!.InnerExceptions),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        var server = Accept();
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(path);
            await using var runtime = new PluginTaskRuntime([new BuiltInBrightnessPlugin(), new HardwareBrightnessPlugin()], store, sid, activity.Engine);
            await runtime.InitializeAsync(); var visibleSettings = (await runtime.SaveBrightnessWheelAsync(new(Enabled: true, CoordinateAutomatic: coordinated), limit.Token)).Settings;
            await ready.Task.WaitAsync(limit.Token); wheel.TrySetResult();
            if (!coordinated)
            {
                await marked.Task.WaitAsync(limit.Token);
                Assert.False(invoked.Task.IsCompleted); Assert.True(runtime.ReadBrightnessWheel().ManualTakenOver);
                complete.TrySetResult();
            }
            await invoked.Task.WaitAsync(limit.Token);
            Assert.Equal(!coordinated, runtime.ReadBrightnessWheel().ManualTakenOver);
            while (runtime.ReadBrightnessWheel().Running) await Task.Delay(5, limit.Token);
            var live = runtime.ReadBrightnessWheel(); Assert.Contains("synthetic strict", live.LastResult);
            if (resumeSucceeds)
            {
                var disabled = await runtime.SaveBrightnessWheelAsync(visibleSettings with { Enabled = false }, limit.Token);
                Assert.False(disabled.Listening); Assert.False(disabled.ManualTakenOver); Assert.False(disabled.Settings.Enabled);
                Assert.Contains(coordinated ? "synthetic strict" : "恢复自动亮度", disabled.LastResult);
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveBrightnessWheelAsync(visibleSettings, limit.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => runtime.SaveBrightnessWheelAsync(visibleSettings with { Enabled = false }, limit.Token));
                Assert.True(runtime.ReadBrightnessWheel().ManualTakenOver);
                Assert.True(runtime.ReadBrightnessWheel().Settings.Enabled);
            }
            await disconnected.Task.WaitAsync(limit.Token);
            Assert.Empty((await runtime.ReadAsync(default)).Tasks);
            await runtime.EnsureSafeToStopAsync(limit.Token);
            serverStop.Cancel(); await server; await Task.WhenAll(clients);
        }
        finally
        {
            limit.Cancel();
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task SettingsAndManualTakeoverPersistAsHiddenInternalTaskAndCannotBeChangedAsAnOrdinaryRule()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-wheel-synthetic-" + Guid.NewGuid().ToString("N"), "tasks.db");
        try
        {
            using var activity = new AutomationFixture();
            using (var store = new SqliteAutomationTaskStore(path))
            {
                await using (var runtime = new PluginTaskRuntime([new BuiltInBrightnessPlugin(), new HardwareBrightnessPlugin()], store, "synthetic", activity.Engine))
                {
                    await runtime.InitializeAsync();
                    var saved = await runtime.SaveBrightnessWheelAsync(new(Key: "B", Modifiers: ShortcutModifiers.Control | ShortcutModifiers.Shift, SmoothMilliseconds: 200, WritesPerSecond: 60), default);
                    Assert.Equal(1, saved.Settings.Version); Assert.False(saved.Listening); Assert.False(saved.ManualTakenOver);
                    Assert.Empty((await runtime.ReadAsync(default)).Tasks);
                    var task = Assert.Single(await store.ListAsync());
                    await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveAsync(task, default));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DeleteAsync(task.Id, task.Version, default));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveBrightnessWheelAsync(new(), default));
                }
                var persisted = Assert.Single(await store.ListAsync());
                await store.SaveAsync(persisted with { Detection = persisted.Detection with { Parameters = persisted.Detection.Parameters.SetItem("manual", "true") } }, default);
            }
            using (var store = new SqliteAutomationTaskStore(path))
            await using (var runtime = new PluginTaskRuntime([new PowerStatePlugin(), new BuiltInBrightnessPlugin(), new HardwareBrightnessPlugin()], store, "synthetic", activity.Engine))
            {
                await runtime.InitializeAsync(); var restored = runtime.ReadBrightnessWheel();
                Assert.True(restored.ManualTakenOver); Assert.Equal(60, restored.Settings.WritesPerSecond); Assert.Equal("B", restored.Settings.Key);
                Assert.Empty((await runtime.ReadAsync(default)).Tasks);
                var key = new ResourceKey(HardwareBrightnessRestoreCapabilities.Set, "brightness", ResourceScope.Session("synthetic", System.Diagnostics.Process.GetCurrentProcess().SessionId));
                Assert.True(runtime.Authorize("synthetic", key, CapabilityOperation.ApplyDesired).Allowed);
                Assert.True(runtime.Authorize("synthetic", key, CapabilityOperation.ReleaseOverride).Allowed);
                Assert.False(runtime.Authorize("other", key, CapabilityOperation.Override).Allowed);
                var rule = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic brightness during manual takeover", false,
                    new(PowerStateCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("mode", "ac").Add("threshold", "20")),
                    new(HardwareBrightnessRestoreCapabilities.Set, 1, ImmutableDictionary<string, string>.Empty.Add("target", "primary").Add("percent", "40").Add("delaySeconds", "0")
                        .Add("fadeEnabled", "true").Add("fadeDurationSeconds", "1").Add("fadeWritesPerSecond", "10")));
                var first = await runtime.SaveAsync(rule, default);
                var updated = await runtime.SaveAsync(first with { Action = first.Action with { Parameters = first.Action.Parameters.SetItem("fadeWritesPerSecond", "18") } }, default);
                Assert.Equal(2, updated.Version);
                var visible = Assert.Single((await runtime.ReadAsync(default)).Tasks);
                var persistedRule = Assert.Single(await store.ListAsync(), t => t.Id == rule.Id);
                Assert.Equal(updated.Version, visible.Version); Assert.Equal(visible.Version, persistedRule.Version);
                Assert.Equal("18", visible.Action.Parameters["fadeWritesPerSecond"]); Assert.Equal("18", persistedRule.Action.Parameters["fadeWritesPerSecond"]);
                Assert.True(runtime.ReadBrightnessWheel().ManualTakenOver);
                var blocked = await runtime.ApplyDesiredAsync(new(key, StateValue.From(RestorableSettingInvocations.Schema, RestorableSettingInvocations.Encode(updated.Action)), "synthetic", 0, 1, "synthetic condition ends"), default);
                Assert.Equal(OutcomeStatus.Rejected, blocked.Status); Assert.Contains("手动接管", blocked.Reason);
                await runtime.EnsureSafeToStopAsync(default);
            }
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
