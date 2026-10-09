using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class ApplicationAudioTests
{
    private static string App(string directory = "selected") => Path.Combine(Path.GetTempPath(), directory, "Player.exe");
    private static TaskInvocation Invocation(string operation = "volume") => new(ApplicationAudioCapabilities.Set, 1,
        new Dictionary<string, string> { ["executable"] = App(), ["operation"] = operation, ["percent"] = "30", ["delaySeconds"] = "12" }.ToImmutableDictionary());

    [Fact]
    public void RegistryDeclaresRealCombinationsAndTimeWindowWithCanonicalFullPathResources()
    {
        var action = new ApplicationAudioTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Equal(TaskFieldKind.ExecutableSelection, action.Descriptor.Fields.Single(f => f.Id == "executable").Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation()).MinimumDuration);
        Assert.Equal(action.Compile(Invocation("mute"), scope).Key, action.Compile(Invocation("unmute"), scope).Key);
        var two = Invocation() with { Parameters = Invocation().Parameters.SetItem("executable", App() + "\n" + App("other")) };
        var reverse = two with { Parameters = two.Parameters.SetItem("executable", App("other") + "\n" + App()) };
        Assert.Equal(action.Compile(two, scope).Key, action.Compile(reverse, scope).Key);
        Assert.NotEqual(action.Compile(Invocation(), scope).Key, action.Compile(Invocation() with { Parameters = Invocation().Parameters.SetItem("executable", App("other")) }, scope).Key);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<ApplicationAudioPlugin>());
        Assert.Contains(plugin.Combinations, c => c.DetectionId == ForegroundCapabilities.Active && c.ActionId == ApplicationAudioCapabilities.Set);
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
    }

    [Theory]
    [InlineData("operation", "toggle")]
    [InlineData("percent", "101")]
    [InlineData("percent", "30.5")]
    [InlineData("delaySeconds", "-1")]
    [InlineData("executable", "Player.exe")]
    public void InvalidCommandsAreRejectedBeforeNativeAdmission(string field, string value) =>
        Assert.Throws<ArgumentException>(() => ApplicationAudioCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem(field, value) }));

    [Theory]
    [InlineData("volume")]
    [InlineData("mute")]
    [InlineData("unmute")]
    public void EveryMatchingActiveSessionIsChangedOnceButSameFilenameAtOtherPathAndOtherUserAreUntouched(string operation)
    {
        var first = new Session(App()); var second = new Session(App()); var differentPath = new Session(App("other"));
        var otherUser = new Session(App()) { Identity = AudioSessionIdentity.Other }; var inactive = new Session(App()) { IsActive = false };
        var result = ApplicationAudioOperation.Apply([first, second, differentPath, otherUser, inactive], [App()], operation, 30, default);
        Assert.True(result.Success); Assert.Contains("确认2个会话", result.Reason);
        foreach (var session in new[] { first, second })
        {
            Assert.Equal(operation == "volume" ? 1 : 0, session.VolumeWrites); Assert.Equal(operation == "volume" ? 0 : 1, session.MuteWrites);
            Assert.Equal(operation == "volume" ? .3f : .7f, session.Volume); Assert.Equal(operation != "unmute", session.Muted);
        }
        foreach (var session in new[] { differentPath, otherUser, inactive }) { Assert.Equal(0, session.VolumeWrites); Assert.Equal(0, session.MuteWrites); }
    }

    [Fact]
    public void MissingTargetIsPartialFailureAndInactiveSessionsAreNotPreconfiguredForFuturePlayback()
    {
        var playing = new Session(App()); var paused = new Session(App("other")) { IsActive = false };
        var result = ApplicationAudioOperation.Apply([playing, paused], [App(), App("other")], "volume", 30, default);
        Assert.False(result.Success); Assert.Contains("部分完成", result.Reason); Assert.Contains("未播放或无匹配活动会话", result.Reason);
        Assert.Equal(1, playing.VolumeWrites); Assert.Equal(0, paused.VolumeWrites);
        Assert.False(ApplicationAudioOperation.Apply([], [App()], "mute", 30, default).Success);
    }

    [Fact]
    public void StaleIdentityOnAnInactiveUnrelatedSessionDoesNotTurnAConfirmedTargetIntoFalseFailure()
    {
        var playing = new Session(App()); var stale = new Session(App("old")) { IsActive = false, Identity = AudioSessionIdentity.Unknown };
        var result = ApplicationAudioOperation.Apply([stale, playing], [App()], "mute", 30, default);
        Assert.True(result.Success); Assert.Equal(0, stale.IdentityReads); Assert.Equal(1, playing.MuteWrites);
    }

    [Fact]
    public void UnknownOrChangedIdentityIsSkippedAndPreventsClaimingAllSessionsWereProcessed()
    {
        var unknown = new Session(App()) { Identity = AudioSessionIdentity.Unknown }; var changed = new Session(App()) { ChangeIdentity = true };
        var known = new Session(App());
        var result = ApplicationAudioOperation.Apply([unknown, changed, known], [App()], "mute", 30, default);
        Assert.False(result.Success); Assert.Contains("部分完成", result.Reason); Assert.Contains("无法核对", result.Reason);
        Assert.Equal(0, unknown.MuteWrites); Assert.Equal(0, changed.MuteWrites); Assert.Equal(1, known.MuteWrites);
    }

    [Fact]
    public void SessionThatStopsPlayingBeforeWriteIsReportedAsFailureWithoutChangingFuturePlayback()
    {
        var stopped = new Session(App()) { DeactivateOnRecheck = true };
        var result = ApplicationAudioOperation.Apply([stopped], [App()], "volume", 30, default);
        Assert.False(result.Success); Assert.Equal(0, stopped.VolumeWrites); Assert.Equal(.7f, stopped.Volume);
    }

    [Fact]
    public void IndividualReadbackAndWriteFailuresStillProcessOtherTargetsWithoutRetriesOrRollback()
    {
        var disagreement = new Session(App()) { OverrideReadback = true }; var denied = new Session(App()) { FailWrite = true }; var good = new Session(App("other"));
        var result = ApplicationAudioOperation.Apply([disagreement, denied, good], [App(), App("other")], "volume", 30, default);
        Assert.False(result.Success); Assert.Contains("部分完成", result.Reason); Assert.Contains("2个失败", result.Reason);
        Assert.Equal(1, disagreement.VolumeWrites); Assert.Equal(.3f, disagreement.Volume); Assert.Equal(1, denied.VolumeWrites); Assert.Equal(1, good.VolumeWrites);
    }

    [Fact]
    public void CancellationStopsLaterWritesAndNeverRestoresAlreadyChangedSessions()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); var untouched = new Session(App());
        Assert.ThrowsAny<OperationCanceledException>(() => ApplicationAudioOperation.Apply([untouched], [App()], "volume", 30, stop.Token)); Assert.Equal(0, untouched.VolumeWrites);
        using var after = new CancellationTokenSource(); var first = new Session(App()) { AfterWrite = after.Cancel }; var later = new Session(App());
        Assert.ThrowsAny<OperationCanceledException>(() => ApplicationAudioOperation.Apply([first, later], [App()], "volume", 30, after.Token));
        Assert.Equal(1, first.VolumeWrites); Assert.Equal(.3f, first.Volume); Assert.Equal(0, later.VolumeWrites);
    }

    private sealed class Session(string path) : IApplicationAudioSession
    {
        internal AudioSessionIdentity Identity = AudioSessionIdentity.Owned;
        internal bool ChangeIdentity, OverrideReadback, FailWrite, DeactivateOnRecheck; internal Action? AfterWrite; internal int IdentityReads, VolumeWrites, MuteWrites, ActiveReads;
        internal float Volume = .7f; internal bool Muted = true;
        private bool active = true;
        public bool IsActive { get => active && !(DeactivateOnRecheck && ++ActiveReads > 1); init => active = value; }
        public AudioSessionIdentity Identify(out string? value) { value = path; IdentityReads++; return ChangeIdentity && IdentityReads > 1 ? AudioSessionIdentity.Unknown : Identity; }
        public void SetVolume(float value) { VolumeWrites++; if (FailWrite) throw new UnauthorizedAccessException(); Volume = value; AfterWrite?.Invoke(); }
        public float GetVolume() => OverrideReadback ? .9f : Volume;
        public void SetMuted(bool value) { MuteWrites++; if (FailWrite) throw new UnauthorizedAccessException(); Muted = value; AfterWrite?.Invoke(); }
        public bool GetMuted() => OverrideReadback ? !Muted : Muted;
    }
}
