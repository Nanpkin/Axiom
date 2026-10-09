using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class AudioPlaybackAdjustTests
{
    private static TaskInvocation Invocation(string operation = "increase", string step = "5", string delay = "0") => new(AudioPlaybackAdjustCapabilities.Adjust, 1,
        new Dictionary<string, string> { ["operation"] = operation, ["step"] = step, ["delaySeconds"] = delay }.ToImmutableDictionary());

    [Fact]
    public void LiveRegistryShortcutTemplatesAndImportRequireReviewedStrictRelativeSettings()
    {
        var modules = BuiltInAutomationPlugins.Create(); Assert.Single(modules.OfType<AudioPlaybackAdjustPlugin>());
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var templates = AutomationTemplates.Build(catalog).Where(t => t.Draft.Action.CapabilityId == AudioPlaybackAdjustCapabilities.Adjust).ToArray();
        Assert.Equal(2, templates.Length); Assert.All(templates, t => { Assert.True(t.Draft.Paused); Assert.Equal("5", t.Draft.Action.Parameters["step"]); });
        var scope = ResourceScope.Session("owner", 1); var action = new AudioPlaybackAdjustTaskAction();
        Assert.True(action.SupportsTimeWindow); Assert.Equal(TimeSpan.FromSeconds(60), action.Timing(Invocation(delay: "60")).MinimumDuration);
        var old = new TaskInvocation(AudioPlaybackCapabilities.Set, 1, new Dictionary<string, string> { ["operation"] = "volume", ["percent"] = "30", ["delaySeconds"] = "0" }.ToImmutableDictionary());
        Assert.Equal(new AudioPlaybackTaskAction().Compile(old, scope).Key.ResourceId, action.Compile(Invocation(), scope).Key.ResourceId);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var detection = new TaskInvocation(HotkeyCapabilities.Pressed, 1, new Dictionary<string, string> { ["chords"] = "Ctrl+Alt+F9" }.ToImmutableDictionary());
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "调整音量", "合成建议", detection, Invocation());
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        foreach (var invalid in new[] { Invocation(step: "0"), Invocation(step: "101"), Invocation(step: "0.5"), Invocation(step: " 5"), Invocation("toggle"), Invocation(delay: "86401") })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = invalid }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => AudioPlaybackAdjustCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("unmute", "true") }));
    }

    [Fact]
    public void RelativeReadWriteReadbackClampsAtLimitsAndNeverTouchesMute()
    {
        foreach (var (initial, operation, step, expected) in new[] { (.4f, "increase", 5, .45f), (.4f, "decrease", 5, .35f), (.99f, "increase", 5, 1f), (.01f, "decrease", 5, 0f), (1f, "increase", 100, 1f), (0f, "decrease", 100, 0f) })
        {
            var endpoint = new Endpoint { Volume = initial }; var result = AudioPlaybackAdjustOperation.Apply(endpoint, operation, step, default);
            Assert.True(result.Success); Assert.Equal(expected, endpoint.Volume, 5); Assert.Equal(2, endpoint.Reads); Assert.Equal(1, endpoint.Writes);
            Assert.Equal(0, endpoint.MuteCalls); Assert.True(endpoint.Muted); Assert.Contains("静音状态未更改", result.Reason);
        }
    }

    [Fact]
    public void InvalidCurrentVolumePreventsWriteAndBadReadbackNeverRetriesOrRestores()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.01f, 1.01f })
        {
            var endpoint = new Endpoint { Volume = value }; Assert.False(AudioPlaybackAdjustOperation.Apply(endpoint, "increase", 5, default).Success);
            Assert.Equal(1, endpoint.Reads); Assert.Equal(0, endpoint.Writes); Assert.Equal(0, endpoint.MuteCalls);
        }
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, -.01f, 1.01f, .9f })
        {
            var endpoint = new Endpoint { Readback = value }; var result = AudioPlaybackAdjustOperation.Apply(endpoint, "decrease", 5, default);
            Assert.False(result.Success); Assert.Contains("不重试、不恢复", result.Reason); Assert.Equal(1, endpoint.Writes); Assert.Equal(2, endpoint.Reads);
            Assert.Equal(.35f, endpoint.Volume, 5); Assert.Equal(0, endpoint.MuteCalls);
        }
    }

    [Fact]
    public void CancellationBeforeAndAfterBaselinePreventsWriteWhileAfterWriteDoesNotUndoIt()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Endpoint();
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackAdjustOperation.Apply(untouched, "increase", 5, before.Token));
        Assert.Equal(0, untouched.Reads); Assert.Equal(0, untouched.Writes);
        using var reading = new CancellationTokenSource(); var canceled = new Endpoint { AfterRead = reading.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackAdjustOperation.Apply(canceled, "increase", 5, reading.Token)); Assert.Equal(0, canceled.Writes);
        using var after = new CancellationTokenSource(); var requested = new Endpoint { AfterWrite = after.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => AudioPlaybackAdjustOperation.Apply(requested, "increase", 5, after.Token));
        Assert.Equal(1, requested.Reads); Assert.Equal(1, requested.Writes); Assert.Equal(.45f, requested.Volume, 5); Assert.True(requested.Muted);
    }

    private sealed class Endpoint : IAudioPlaybackEndpoint
    {
        internal float Volume = .4f; internal bool Muted = true; internal float? Readback;
        internal int Reads, Writes, MuteCalls; internal Action? AfterRead, AfterWrite;
        public float GetVolume() { Reads++; AfterRead?.Invoke(); return Reads > 1 && Readback.HasValue ? Readback.Value : Volume; }
        public void SetVolume(float value) { Writes++; Volume = value; AfterWrite?.Invoke(); }
        public bool GetMuted() { MuteCalls++; return Muted; }
        public void SetMuted(bool value) { MuteCalls++; Muted = value; }
    }
}
