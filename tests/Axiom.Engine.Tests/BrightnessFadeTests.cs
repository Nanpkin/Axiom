using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[Collection("Brightness native ownership")]
public sealed class BrightnessFadeTests
{
    [Fact]
    public void SixCapabilitiesAcceptLegacyAndOptionalFadeButRejectMalformedOrExtraParameters()
    {
        foreach (var descriptor in Descriptors())
        {
            var legacy = descriptor.Fields.Where(f => f.Id is not ("fadeEnabled" or "fadeDurationSeconds"))
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!);
            var invocation = new TaskInvocation(descriptor.Id, 1, legacy);
            Validate(invocation); Assert.False(BrightnessFadeSettings.Parse(legacy).Enabled);
            Assert.Equal(TimeSpan.FromSeconds(10), BrightnessFadeOptions.Parse(legacy).OperationTimeout);
            Assert.Equal("false", descriptor.Fields.Single(f => f.Id == "fadeEnabled").DefaultValue);
            Assert.Equal("3", descriptor.Fields.Single(f => f.Id == "fadeDurationSeconds").DefaultValue);
            Assert.All(descriptor.Fields.Where(f => f.Id.StartsWith("fade", StringComparison.Ordinal)), f => Assert.Equal("渐变设置", f.Group));
            foreach (var duration in new[] { "1", "3", "15" })
            {
                var configured = legacy.Add("fadeEnabled", "true").Add("fadeDurationSeconds", duration);
                Validate(invocation with { Parameters = configured });
                Assert.True(BrightnessFadeSettings.Parse(configured).Enabled);
                Assert.Equal(BrightnessFadeSettings.Parse(configured).OperationTimeout, BrightnessFadeOptions.Parse(configured).OperationTimeout);
            }
            Validate(invocation with { Parameters = legacy.Add("fadeEnabled", "true") });
            Validate(invocation with { Parameters = legacy.Add("fadeDurationSeconds", "5") });
            foreach (var invalid in new[] { "0", "16", "1.5", " 3", "", "-1" })
                Assert.Throws<ArgumentException>(() => Validate(invocation with { Parameters = legacy.Add("fadeDurationSeconds", invalid) }));
            foreach (var invalid in new[] { "True", "1", "", "false " })
                Assert.Throws<ArgumentException>(() => Validate(invocation with { Parameters = legacy.Add("fadeEnabled", invalid) }));
            Assert.Throws<ArgumentException>(() => Validate(invocation with { Parameters = legacy.Add("fadeSpeed", "fast") }));
        }
        var modules = BuiltInAutomationPlugins.Create();
        foreach (var binding in modules.OfType<BuiltInBrightnessPlugin>().Single().Bindings.Concat(modules.OfType<HardwareBrightnessPlugin>().Single().Bindings))
            Assert.Equal(TimeSpan.FromSeconds(binding.Descriptor.DesiredState ? 40 : 30), binding.Descriptor.Timeout);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WmiAndDdcAbsoluteAndRelativeFadeHaveMonotonicDistinctConfirmedStepsAndExactEndpoints(bool hardware, bool relative)
    {
        var clock = new ManualTimeProvider(); var api = new Display(clock); var fade = new BrightnessFadeOptions(true, 3);
        void Wait(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); clock.Advance(delay); }
        var result = hardware
            ? relative ? HardwareBrightnessOperation.Adjust(api, "primary", "increase", 40, default, fade, clock, Wait) : HardwareBrightnessOperation.Apply(api, "primary", 90, default, fade, clock, Wait)
            : relative ? BuiltInBrightnessOperation.Adjust(api, "increase", 40, default, fade: fade, time: clock, wait: Wait) : BuiltInBrightnessOperation.Apply(api, 90, default, fade: fade, time: clock, wait: Wait);
        Assert.True(result.Success); Assert.Equal(relative ? 90u : 90u, api.Current);
        Assert.InRange(api.Writes.Count, 2, 12); Assert.Equal(api.Writes.Distinct(), api.Writes);
        Assert.Equal(api.Writes.Order(), api.Writes); Assert.Equal(90u, api.Writes[^1]);
        Assert.All(api.WriteTimes.Zip(api.WriteTimes.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(250)));
        Assert.Equal(1, hardware ? api.Acquisitions : api.Discoveries); Assert.Equal(hardware ? 1 : 0, api.Closes);
        Assert.Equal(TimeSpan.FromMilliseconds(hardware ? 3750 : 3000), clock.GetElapsedTime(0));
    }

    [Fact]
    public void HardwareFadeWaitsAfterEachAcceptedWriteBeforeItsFirstConfirmationAndLegacyDoesNotWait()
    {
        foreach (var captured in new[] { false, true }) foreach (var enabled in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); var api = new Display(clock); var settings = new BrightnessFadeOptions(enabled, 3);
            void Wait(TimeSpan delay, CancellationToken token)
            { api.Calls.Add("wait:" + delay.TotalMilliseconds); clock.Advance(delay); token.ThrowIfCancellationRequested(); }
            if (captured)
            {
                var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => true, default, out _)!;
                BrightnessFade.Apply([(target, 95)], settings, default, clock, Wait);
                Assert.True(target.Transaction.Owned); Assert.Equal(95u, api.Current);
            }
            else Assert.True(HardwareBrightnessOperation.Apply(api, "primary", 95, default, settings, clock, Wait).Success);
            for (var i = 0; i < api.Calls.Count; i++)
            {
                if (api.Calls[i] != "write") continue;
                Assert.Equal(enabled ? "wait:500" : "read", api.Calls[i + 1]);
                if (enabled) Assert.Equal("read", api.Calls[i + 2]);
            }
            Assert.Equal(enabled ? api.Writes.Count : 0, api.Calls.Count(c => c == "wait:500"));
            if (!enabled) Assert.DoesNotContain(api.Calls, c => c.StartsWith("wait:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CancellationDuringHardwareFadeSettleDoesNotConfirmOrIssueAnotherWrite()
    {
        foreach (var captured in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); var api = new Display(clock); using var stop = new CancellationTokenSource();
            void Wait(TimeSpan delay, CancellationToken token)
            {
                api.Calls.Add("wait:" + delay.TotalMilliseconds); clock.Advance(delay);
                if (delay == TimeSpan.FromMilliseconds(500)) stop.Cancel();
                token.ThrowIfCancellationRequested();
            }
            if (captured)
            {
                var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => true, default, out _)!;
                Assert.ThrowsAny<OperationCanceledException>(() => BrightnessFade.Apply([(target, 95)], new(true, 3), stop.Token, clock, Wait));
                Assert.False(target.Transaction.Owned); Assert.True(target.Transaction.Release(default).Success);
            }
            else Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(api, "primary", 95, stop.Token, new(true, 3), clock, Wait));
            Assert.Single(api.Writes); Assert.Equal("wait:500", api.Calls[^1]);
            Assert.DoesNotContain(api.Calls.Skip(api.Calls.IndexOf("write") + 1), c => c == "read");
        }
    }

    [Fact]
    public void ZeroDistanceFadeConfirmsCurrentOwnershipWithoutWaitingOrWriting()
    {
        foreach (var externallyChanged in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); uint current = externallyChanged ? 17u : 50u;
            var reads = 0; var writes = 0; var waits = 0;
            var target = new BrightnessRestoreTarget(50, p => (uint)p, () => { reads++; return current; },
                value => { writes++; current = value; return true; });
            BrightnessFade.Apply([(target, 50)], new(true, 15), default, clock, (_, _) => waits++);
            Assert.InRange(reads, 1, 2); Assert.Equal(0, writes); Assert.Equal(0, waits);
            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(0));
            Assert.Equal(!externallyChanged, target.Transaction.Owned);
            Assert.Equal(externallyChanged ? 17u : 50u, current);
        }
    }

    [Fact]
    public void CoarseNativeMappingWritesEachValueOnceAndSlowCallsSkipFramesWithoutCatchup()
    {
        var clock = new ManualTimeProvider(); uint current = 0; var values = new List<uint>(); var timestamps = new List<TimeSpan>();
        var target = new BrightnessRestoreTarget(0, p => (uint)p, () => current, value =>
        { timestamps.Add(clock.GetElapsedTime(0)); values.Add(value); current = value; clock.Advance(TimeSpan.FromMilliseconds(600)); return true; });
        BrightnessFade.Apply([(target, 2)], new(true, 3), default, clock, (delay, _) => clock.Advance(delay));
        Assert.Equal(new uint[] { 1, 2 }, values);
        Assert.True(timestamps[1] - timestamps[0] >= TimeSpan.FromMilliseconds(850));
        Assert.Equal(2u, current); Assert.True(clock.GetElapsedTime(0) < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MidTransitionExternalChangeOrCancellationStopsAllRemainingWrites(bool cancel)
    {
        var clock = new ManualTimeProvider(); var api = new Display(clock); using var stop = new CancellationTokenSource(); var waits = 0;
        void Wait(TimeSpan delay, CancellationToken token)
        {
            clock.Advance(delay);
            if (++waits == 2) { if (cancel) stop.Cancel(); else api.Current = 17; }
            token.ThrowIfCancellationRequested();
        }
        if (cancel) Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(api, "primary", 90, stop.Token, new(true, 3), clock, Wait));
        else Assert.False(HardwareBrightnessOperation.Apply(api, "primary", 90, stop.Token, new(true, 3), clock, Wait).Success);
        Assert.Single(api.Writes); Assert.Equal(cancel ? api.Writes[0] : 17u, api.Current); Assert.Equal(1, api.Closes);
    }

    [Fact]
    public void UnconfirmedStepIsNeverRetriedOrRestored()
    {
        var clock = new ManualTimeProvider(); var api = new Display(clock) { RefuseReadback = true };
        Assert.False(BuiltInBrightnessOperation.Apply(api, 90, default, fade: new(true, 3), time: clock, wait: (delay, _) => clock.Advance(delay)).Success);
        Assert.Single(api.Writes); Assert.NotEqual(90u, api.Current);
    }

    [Fact(Timeout = 10000)]
    public async Task FadeReleaseCompletesAfterExactBaselineConfirmationWithoutExtraReadsAndRejectsUnconfirmedEndpoint()
    {
        foreach (var unconfirmed in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); uint current = 100; var restoring = false; var baselineReads = 0;
            var writes = new List<uint>();
            var session = await BrightnessRestoreSession.CreateAsync(_ =>
            {
                var target = new BrightnessRestoreTarget(100, p => (uint)p, () =>
                {
                    if (restoring && current == 100)
                    {
                        baselineReads++;
                        if (baselineReads > 1) throw new IOException("No native read is allowed after endpoint confirmation.");
                        if (unconfirmed) return null;
                    }
                    return current;
                }, value => { current = value; writes.Add(value); return true; });
                return new([target], 0, 0, _ => { }, () => { });
            }, null, TimeSpan.FromSeconds(10), default, clock, (_, _) => clock.Advance(TimeSpan.FromSeconds(3)));
            try
            {
                Assert.True((await session.ApplyAsync(new Dictionary<string, string>
                { ["percent"] = "95", ["fadeEnabled"] = "true", ["fadeDurationSeconds"] = "3" }, default)).Owned);
                restoring = true;
                var released = await session.ReleaseAsync(default);
                Assert.Equal(!unconfirmed, released.Success); Assert.False(released.Owned);
                Assert.Equal(1, baselineReads); Assert.Equal(new uint[] { 95, 100 }, writes);
            }
            finally { await session.DisposeAsync(); }
            Assert.Equal(1, baselineReads); Assert.Equal(new uint[] { 95, 100 }, writes);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task RestoreFadesBothDirectionsPreservesOriginalBaselineAcrossUpdatesAndManualTakeover()
    {
        foreach (var takeover in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); var api = new Display(clock); var captures = 0;
            var session = await BrightnessRestoreSession.CreateAsync(_ =>
            {
                captures++; var target = new BrightnessRestoreTarget(50, p => (uint)p, () => api.Current, value => api.Write(value));
                return new([target], 0, 0, _ => { }, () => { });
            }, null, TimeSpan.FromSeconds(10), default, clock, (delay, _) => clock.Advance(delay));
            try
            {
                Dictionary<string, string> Parameters(int percent) => new() { ["percent"] = percent.ToString(), ["fadeEnabled"] = "true", ["fadeDurationSeconds"] = "3" };
                Assert.True((await session.ApplyAsync(Parameters(90), default)).Owned);
                Assert.InRange(api.Writes.Count, 2, 12); Assert.Equal(90u, api.Current);
                Assert.True((await session.ApplyAsync(Parameters(70), default)).Owned); Assert.Equal(1, captures);
                var applyCount = api.Writes.Count;
                if (takeover) api.Current = 19;
                var result = await session.ReleaseAsync(default); Assert.True(result.Success);
                Assert.Equal(takeover ? 19u : 50u, api.Current);
                if (takeover) Assert.Equal(applyCount, api.Writes.Count);
                else { Assert.True(api.Writes.Count > applyCount + 1); Assert.Equal(api.Writes.Skip(applyCount).OrderDescending(), api.Writes.Skip(applyCount)); }
            }
            finally { await session.DisposeAsync(); }
        }
    }

    private static IEnumerable<TaskCapability> Descriptors() => [BuiltInBrightnessCapabilities.Descriptor, HardwareBrightnessCapabilities.Descriptor,
        BuiltInBrightnessAdjustCapabilities.Descriptor, HardwareBrightnessAdjustCapabilities.Descriptor,
        BuiltInBrightnessRestoreCapabilities.Descriptor, HardwareBrightnessRestoreCapabilities.Descriptor];
    private static void Validate(TaskInvocation invocation)
    {
        switch (invocation.CapabilityId)
        {
            case BuiltInBrightnessCapabilities.Set: BuiltInBrightnessCapabilities.Validate(invocation); break;
            case HardwareBrightnessCapabilities.Set: HardwareBrightnessCapabilities.Validate(invocation); break;
            case BuiltInBrightnessAdjustCapabilities.Adjust: BuiltInBrightnessAdjustCapabilities.Validate(invocation); break;
            case HardwareBrightnessAdjustCapabilities.Adjust: HardwareBrightnessAdjustCapabilities.Validate(invocation); break;
            case BuiltInBrightnessRestoreCapabilities.Set: BuiltInBrightnessRestoreCapabilities.Validate(invocation); break;
            case HardwareBrightnessRestoreCapabilities.Set: HardwareBrightnessRestoreCapabilities.Validate(invocation); break;
        }
    }
    private sealed class Display(ManualTimeProvider clock) : IBuiltInBrightnessApi, IHardwareBrightnessApi, IHardwareMonitorLease
    {
        internal uint Current = 50; internal bool RefuseReadback; internal int Discoveries, Acquisitions, Closes;
        internal readonly List<uint> Writes = []; internal readonly List<TimeSpan> WriteTimes = [];
        internal readonly List<string> Calls = [];
        public IReadOnlyList<BrightnessPanel> FindPanels() { Discoveries++; return [new("synthetic", BrightnessPanelSupport.Supported)]; }
        public bool Verify(string key) => true;
        public uint Set(string key, byte value) => Write(value) ? 0u : 1u;
        public int? Read(string key) => (int)Current;
        public bool IsAuthorized() => true;
        public IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors() => [new(1, true)];
        public IHardwareMonitorLease Acquire(nint logicalMonitor, int remainingCapacity) { Acquisitions++; return this; }
        public uint? Capabilities(nint monitor) => 2;
        public HardwareBrightnessRange? Read(nint monitor) { Calls.Add("read"); return new(0, Current, 100); }
        public bool Set(nint monitor, uint value) => Write(value);
        internal bool Write(uint value) { Calls.Add("write"); Writes.Add(value); WriteTimes.Add(clock.GetElapsedTime(0)); Current = RefuseReadback ? value + 1 : value; return true; }
        public IReadOnlyList<nint> Handles => [11];
        public void Dispose() => Closes++;
    }
}
