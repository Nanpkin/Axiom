using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class AudioPlaybackTests
{
    private static TaskInvocation Invocation(string operation = "mute") => new(AudioPlaybackCapabilities.Set, 1,
        new Dictionary<string, string> { ["operation"] = operation, ["percent"] = "30", ["delaySeconds"] = "0" }.ToImmutableDictionary());
    [Fact]
    public void AllPlaybackOperationsUseSameArbitratedResourceAndRejectOutOfRangeInput()
    {
        var compiler = new AudioPlaybackTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.Equal(compiler.Compile(Invocation("volume"), scope).Key, compiler.Compile(Invocation("mute"), scope).Key);
        Assert.Equal(compiler.Compile(Invocation("mute"), scope).Key, compiler.Compile(Invocation("unmute"), scope).Key);
        foreach (var (field, value) in new[] { ("operation", "toggle"), ("percent", "101"), ("percent", "30.5"), ("delaySeconds", "-1"), ("delaySeconds", "86401") })
            Assert.Throws<ArgumentException>(() => compiler.Compile(Invocation() with { Parameters = Invocation().Parameters.SetItem(field, value) }, scope));
        Assert.Contains(new AudioPlaybackPlugin().Combinations, c => c.DetectionId == SystemEventCapabilities.Event && c.ActionId == AudioPlaybackCapabilities.Set);
    }
    [Fact]
    public void MicrophoneActionsHaveSeparateResourceAndRejectVolumeOrToggleRequests()
    {
        var invocation = new TaskInvocation(MicrophoneCapabilities.Set, 1,
            new Dictionary<string, string> { ["operation"] = "mute", ["delaySeconds"] = "0" }.ToImmutableDictionary());
        var compiler = new MicrophoneTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.Equal("default-communications-input", compiler.Compile(invocation, scope).Key.ResourceId);
        Assert.NotEqual(new AudioPlaybackTaskAction().Compile(Invocation(), scope).Key, compiler.Compile(invocation, scope).Key);
        foreach (var operation in new[] { "volume", "toggle", "input-id" })
            Assert.Throws<ArgumentException>(() => compiler.Compile(invocation with { Parameters = invocation.Parameters.SetItem("operation", operation) }, scope));
        Assert.Throws<ArgumentException>(() => MicrophoneCapabilities.Validate(invocation with { Parameters = invocation.Parameters.Add("percent", "50") }));
    }
    [Theory]
    [InlineData("mute", true)]
    [InlineData("unmute", false)]
    public void MicrophoneMuteReadbackNeverWritesInputGainAndNamesTheLimitedTarget(string operation, bool expected)
    {
        var endpoint = new Endpoint(); var result = AudioPlaybackOperation.Apply(endpoint, operation, 0, default, "默认通信麦克风");
        Assert.True(result.Success); Assert.Contains("默认通信麦克风", result.Reason);
        Assert.Equal(expected, endpoint.Muted); Assert.Equal(1, endpoint.MuteWrites); Assert.Equal(0, endpoint.VolumeWrites); Assert.Equal(.7f, endpoint.Volume);
    }
    [Theory]
    [InlineData("volume", 1, 0)]
    [InlineData("mute", 0, 1)]
    [InlineData("unmute", 0, 1)]
    public void OneShotChangesOnlyRequestedSettingAndReadbackConfirmsIt(string operation, int volumeWrites, int muteWrites)
    {
        var endpoint = new Endpoint(); var result = AudioPlaybackOperation.Apply(endpoint, operation, 30, default);
        Assert.True(result.Success); Assert.Equal(volumeWrites, endpoint.VolumeWrites); Assert.Equal(muteWrites, endpoint.MuteWrites);
        if (operation == "volume") { Assert.Equal(.3f, endpoint.Volume); Assert.True(endpoint.Muted); }
        else { Assert.Equal(.7f, endpoint.Volume); Assert.Equal(operation == "mute", endpoint.Muted); }
    }
    [Fact]
    public void ReadbackDisagreementDoesNotRetryOrRestoreOverExternalChanges()
    {
        var endpoint = new Endpoint { OverrideReadback = true };
        Assert.False(AudioPlaybackOperation.Apply(endpoint, "volume", 30, default).Success); Assert.Equal(1, endpoint.VolumeWrites); Assert.Equal(.3f, endpoint.Volume);
        Assert.False(AudioPlaybackOperation.Apply(endpoint, "unmute", 30, default).Success); Assert.Equal(1, endpoint.MuteWrites); Assert.False(endpoint.Muted);
    }
    [Fact]
    public void CancellationBeforeWriteHasNoEffectAndAfterWriteDoesNotRestoreOldState()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var untouched = new Endpoint();
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackOperation.Apply(untouched, "mute", 30, cancellation.Token));
        Assert.Equal(0, untouched.MuteWrites);
        using var after = new CancellationTokenSource(); var endpoint = new Endpoint { AfterWrite = after.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackOperation.Apply(endpoint, "volume", 30, after.Token));
        Assert.Equal(1, endpoint.VolumeWrites); Assert.Equal(.3f, endpoint.Volume);
    }
    [Fact(Timeout = 10000)]
    public async Task BusyNativeCallRejectsNewWorkAndFailureReleasesAdmission()
    {
        var commands = new BoundedAudioCommand(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var calls = 0;
        var first = commands.RunAsync(_ => { Interlocked.Increment(ref calls); entered.SetResult(); release.Wait(); return (true, "synthetic"); }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var busy = await commands.RunAsync(_ => { Interlocked.Increment(ref calls); return (true, "unexpected"); }, default);
            Assert.False(busy.Success); Assert.Contains("本次未执行", busy.Reason); Assert.Equal(1, calls);
        }
        finally { release.Set(); }
        Assert.True((await first).Success);
        Assert.False((await commands.RunAsync(_ => throw new InvalidOperationException("synthetic unavailable"), default)).Success);
        Assert.True((await commands.RunAsync(_ => (true, "synthetic recovered"), default)).Success);
    }
    [Fact(Timeout = 10000)]
    public async Task TimeoutDoesNotReleaseAdmissionWhileNativeWorkIsStillBlocked()
    {
        var commands = new BoundedAudioCommand(); var time = new ManualTimeProvider();
        var timerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        time.TimerCreated = _ => timerReady.TrySetResult();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var canceled = false;
        var first = commands.RunAsync(token =>
        {
            entered.SetResult(); release.Wait();
            try { canceled = token.IsCancellationRequested; return (true, "late synthetic result"); }
            finally { exiting.SetResult(); }
        }, default, time);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await timerReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            time.Advance(TimeSpan.FromSeconds(5)); var result = await first;
            Assert.False(result.Success); Assert.Contains("结果未确认", result.Reason);
            var busy = await commands.RunAsync(_ => throw new InvalidOperationException("must not start"), default);
            Assert.False(busy.Success); Assert.Contains("本次未执行", busy.Reason);
        }
        finally { release.Set(); await exiting.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(canceled);
    }
    private sealed class Endpoint : IAudioPlaybackEndpoint
    {
        internal float Volume = .7f; internal bool Muted = true; internal int VolumeWrites, MuteWrites; internal bool OverrideReadback; internal Action? AfterWrite;
        public void SetVolume(float value) { VolumeWrites++; Volume = value; AfterWrite?.Invoke(); }
        public float GetVolume() => OverrideReadback ? .9f : Volume;
        public void SetMuted(bool value) { MuteWrites++; Muted = value; AfterWrite?.Invoke(); }
        public bool GetMuted() => OverrideReadback ? !Muted : Muted;
    }
}
