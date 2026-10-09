using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class AudioPlaybackToggleTests
{
    private static TaskInvocation Invocation(string delay = "0") => new(AudioPlaybackToggleCapabilities.Toggle, 1,
        new Dictionary<string, string> { ["delaySeconds"] = delay }.ToImmutableDictionary());

    [Fact]
    public void RealRegistryCompilesShortcutToggleAndStrictImportPreservesExistingExplicitMuteSchema()
    {
        var action = new AudioPlaybackToggleTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Equal("default-playback", action.Compile(Invocation(), scope).Key.ResourceId);
        Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation("12")).MinimumDuration);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<AudioPlaybackTogglePlugin>());
        Assert.Contains(plugin.Combinations, c => c.DetectionId == HotkeyCapabilities.Pressed && c.ActionId == AudioPlaybackToggleCapabilities.Toggle);
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        Assert.DoesNotContain(plugin.Combinations, c => c.DetectionId == EndpointMuteCapabilities.State);
        Assert.DoesNotContain(plugin.Combinations, c => c.DetectionId == AudioCapabilities.Changed);
        foreach (var delay in new[] { "-1", "1.5", "86401" }) Assert.Throws<ArgumentException>(() => AudioPlaybackToggleCapabilities.Validate(Invocation(delay)));
        Assert.Throws<ArgumentException>(() => AudioPlaybackToggleCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("operation", "toggle") }));
        Assert.Throws<ArgumentException>(() => AudioPlaybackCapabilities.Validate(new(AudioPlaybackCapabilities.Set, 1,
            new Dictionary<string, string> { ["operation"] = "toggle", ["percent"] = "30", ["delaySeconds"] = "0" }.ToImmutableDictionary())));

        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        var modules = BuiltInAutomationPlugins.Create();
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var detection = new TaskInvocation(HotkeyCapabilities.Pressed, 1,
            new Dictionary<string, string> { ["chords"] = "Ctrl+Alt+F9" }.ToImmutableDictionary());
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "playback-toggle-shortcut");
        Assert.True(template.Draft.Paused);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        Assert.NotNull(compiler.Compile(template.Draft with { Detection = detection }, "owner", scope));
        string Proposal(TaskInvocation command) => JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id, "existing", "playback", "review", detection, command));
        var draft = UsageAnalysis.Import(Proposal(Invocation()), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(AudioPlaybackToggleCapabilities.Toggle, draft.Action.CapabilityId);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation("86401")), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation() with { Parameters = Invocation().Parameters.Add("device", "untrusted") }), recorder.Session, catalog));
    }

    [Fact]
    public void BothKnownMuteStatesAreReadThenInvertedOnceAndConfirmedWithoutVolumeAccess()
    {
        foreach (var initial in new[] { true, false })
        {
            var endpoint = new Endpoint { Muted = initial };
            var result = AudioPlaybackToggleOperation.Apply(endpoint, default);
            Assert.True(result.Success); Assert.Equal(!initial, endpoint.Muted);
            Assert.Equal(new[] { "get-mute", "set-mute", "get-mute" }, endpoint.Calls);
            Assert.Equal(1, endpoint.Writes);
            if (initial) Assert.Contains("立即发声", result.Reason);
        }
    }

    [Fact]
    public void UnreadableCurrentStateNeverWritesAndDeniedOrUnconfirmedWriteNeverRetriesOrRestores()
    {
        var unreadable = new Endpoint { FailRead = 1 };
        var result = AudioPlaybackToggleOperation.Apply(unreadable, default);
        Assert.False(result.Success); Assert.Contains("本次未修改", result.Reason); Assert.Equal(0, unreadable.Writes);
        var denied = new Endpoint { DenyWrite = true };
        Assert.False(AudioPlaybackToggleOperation.Apply(denied, default).Success); Assert.Equal(1, denied.Writes); Assert.False(denied.Muted);
        var lostReadback = new Endpoint { FailRead = 2 };
        result = AudioPlaybackToggleOperation.Apply(lostReadback, default);
        Assert.False(result.Success); Assert.Contains("可能已修改", result.Reason); Assert.True(lostReadback.Muted); Assert.Equal(1, lostReadback.Writes);
        var changedExternally = new Endpoint { Mismatch = true };
        result = AudioPlaybackToggleOperation.Apply(changedExternally, default);
        Assert.False(result.Success); Assert.Contains("读回不一致", result.Reason); Assert.True(changedExternally.Muted); Assert.Equal(1, changedExternally.Writes);
    }

    [Fact]
    public void CancellationBeforeWriteLeavesStateAndCancellationAfterWriteLeavesNewStateWithoutRestoration()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Endpoint();
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackToggleOperation.Apply(untouched, before.Token)); Assert.Empty(untouched.Calls);
        using var afterRead = new CancellationTokenSource(); var read = new Endpoint { AfterRead = afterRead.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackToggleOperation.Apply(read, afterRead.Token)); Assert.Equal(0, read.Writes);
        using var afterWrite = new CancellationTokenSource(); var written = new Endpoint { AfterWrite = afterWrite.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackToggleOperation.Apply(written, afterWrite.Token));
        Assert.True(written.Muted); Assert.Equal(1, written.Writes); Assert.Equal(new[] { "get-mute", "set-mute" }, written.Calls);
    }

    private sealed class Endpoint : IAudioPlaybackEndpoint
    {
        internal bool Muted, DenyWrite, Mismatch; internal int FailRead, Reads, Writes;
        internal Action? AfterRead, AfterWrite; internal List<string> Calls = [];
        public bool GetMuted()
        {
            Calls.Add("get-mute"); Reads++;
            if (FailRead == Reads) throw new UnauthorizedAccessException();
            AfterRead?.Invoke(); return Mismatch && Writes > 0 ? !Muted : Muted;
        }
        public void SetMuted(bool value)
        {
            Calls.Add("set-mute"); Writes++;
            if (DenyWrite) throw new UnauthorizedAccessException();
            Muted = value; AfterWrite?.Invoke();
        }
        public float GetVolume() => throw new InvalidOperationException("Toggle must not read volume.");
        public void SetVolume(float value) => throw new InvalidOperationException("Toggle must not write volume.");
    }
}
