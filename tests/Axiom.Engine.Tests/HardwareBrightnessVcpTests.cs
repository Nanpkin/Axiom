using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[Collection("Brightness native ownership")]
public sealed class HardwareBrightnessVcpTests
{
    [Fact(Timeout = 10000)]
    public async Task CapturedInterfaceRemainsFixedWhenFadeIsEnabledOrDisabledDuringLease()
    {
        foreach (var initiallyFading in new[] { false, true })
        {
            var time = new ManualTimeProvider(); var api = new NativeApi(initiallyFading); var captures = 0;
            var session = await BrightnessRestoreSession.CreateAsync(captureToken =>
            {
                captures++;
                var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => true, captureToken, out _)!;
                return new([target], 0, 0, _ => { }, () => { });
            }, "primary", TimeSpan.FromSeconds(10), default, time, (delay, _) => time.Advance(delay));
            try
            {
                Dictionary<string, string> Parameters(int percent, bool fade) => new()
                { ["target"] = "primary", ["percent"] = percent.ToString(), ["fadeEnabled"] = fade ? "true" : "false", ["fadeDurationSeconds"] = "3" };
                Assert.True((await session.ApplyAsync(Parameters(70, initiallyFading), default)).Owned);
                Assert.True((await session.ApplyAsync(Parameters(60, !initiallyFading), default)).Owned);
                Assert.True((await session.ReleaseAsync(default)).Success);
                Assert.Equal(50u, api.Current); Assert.Equal(1, captures);
                Assert.All(api.Calls, call => Assert.StartsWith(initiallyFading ? "vcp:" : "legacy:", call));
                Assert.Contains(api.Calls, call => call.EndsWith(":write", StringComparison.Ordinal));
            }
            finally { await session.DisposeAsync(); }
        }
    }

    [Fact]
    public void VcpRangeValidationAndCapturedRangeGuardsRejectUnknownOrUnconfirmedTargets()
    {
        Assert.Equal(new HardwareBrightnessRange(0, 55, 255), HardwareBrightnessOperation.VcpRange(1, 55, 255));
        Assert.Equal(uint.MaxValue, HardwareBrightnessOperation.Map(HardwareBrightnessOperation.VcpRange(1, uint.MaxValue, uint.MaxValue), 100));
        Assert.Null(HardwareBrightnessOperation.VcpRange(1, 0, 0));
        Assert.Null(HardwareBrightnessOperation.VcpRange(1, 101, 100));
        Assert.Null(HardwareBrightnessOperation.VcpRange(0, 50, 100));
        Assert.Null(HardwareBrightnessOperation.VcpRange(2, 50, 100));
        foreach (var fault in new[] { "read", "range", "type", "write", "readback" })
        {
            var api = new NativeApi(true);
            var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => true, default, out _)!;
            if (fault == "read") api.ReadError = 5;
            else if (fault == "range") api.Maximum = 99;
            else if (fault == "type") api.Type = 0;
            else if (fault == "write") api.RefuseWrite = true;
            else api.MismatchReadback = true;
            Assert.False(target.Transaction.Apply(60, default).Owned);
            Assert.True(target.Transaction.Release(default).Success);
            Assert.False(target.Transaction.Owned);
            Assert.Equal(fault is "write" or "readback" ? 1 : 0, api.Calls.Count(call => call == "vcp:write"));
            Assert.All(api.Calls, call => Assert.StartsWith("vcp:", call));
            Assert.DoesNotContain(50u, api.Writes);
        }
    }

    [Fact]
    public void VcpFailureUsesOnlyExistingNarrowReadRetriesAndNeverFallsBackOrRetriesWrites()
    {
        foreach (var error in new[] { 5, HardwareBrightnessReadPolicy.InvalidMessageCommand })
        {
            var reads = 0; var writes = 0; var waits = 0;
            var io = new HardwareBrightnessIo(true, _ => throw new InvalidOperationException("Legacy read must not be used."),
                _ => { reads++; return (null, error); }, (_, _) => throw new InvalidOperationException("Legacy write must not be used."),
                (_, _) => { writes++; return false; });
            Assert.Null(io.Read(11, default, (delay, _) => { Assert.Equal(TimeSpan.FromMilliseconds(150), delay); waits++; }));
            Assert.Equal(error == HardwareBrightnessReadPolicy.InvalidMessageCommand ? 3 : 1, reads);
            Assert.Equal(error == HardwareBrightnessReadPolicy.InvalidMessageCommand ? 2 : 0, waits);
            Assert.False(io.Write(11, 60)); Assert.Equal(1, writes);
        }
    }

    private sealed class NativeApi : IHardwareBrightnessApi
    {
        private readonly HardwareBrightnessIo io;
        internal uint Current = 50, Maximum = 100;
        internal int ReadError, Type = 1;
        internal bool RefuseWrite, MismatchReadback;
        internal readonly List<string> Calls = [];
        internal readonly List<uint> Writes = [];
        internal NativeApi(bool directVcp) => io = new(directVcp, _ => ReadNative("legacy"), _ => ReadNative("vcp"),
            (_, value) => WriteNative("legacy", value), (_, value) => WriteNative("vcp", value));
        private (HardwareBrightnessRange? Range, int Error) ReadNative(string kind)
        {
            Calls.Add(kind + ":read");
            return (ReadError == 0 ? HardwareBrightnessOperation.VcpRange(Type, Current, Maximum) : null, ReadError);
        }
        private bool WriteNative(string kind, uint value)
        {
            Calls.Add(kind + ":write"); Writes.Add(value);
            if (RefuseWrite) return false;
            Current = MismatchReadback ? value + 1 : value; return true;
        }
        public bool IsAuthorized() => true;
        public IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors() => [new(1, true)];
        public IHardwareMonitorLease Acquire(nint logicalMonitor, int remainingCapacity) => throw new InvalidOperationException("Already captured synthetic handle.");
        public uint? Capabilities(nint monitor) => 2;
        public HardwareBrightnessRange? Read(nint monitor) => io.Read(monitor, default);
        public bool Set(nint monitor, uint brightness) => io.Write(monitor, brightness);
    }
}
