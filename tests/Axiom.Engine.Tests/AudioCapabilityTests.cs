using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class AudioCapabilityTests
{
    [Theory]
    [InlineData("output")]
    [InlineData("input")]
    public void AudioChangesCompileAsEventEdgesAndOnlyExplicitAudioObservationRecordsThem(string endpoint)
    {
        var invocation = new TaskInvocation(AudioCapabilities.Changed, 1, ImmutableDictionary<string, string>.Empty.Add("endpoint", endpoint));
        var condition = new AudioTaskDetection().Compile(invocation, ResourceScope.Session("owner", 1));
        Assert.Equal(ConditionKind.Event, condition.Kind); Assert.Equal(endpoint, Assert.Single(condition.Dependencies).ResourceId);
        var now = DateTimeOffset.UtcNow;
        var selected = new UsageRecorder(new(5, false, false, [], AudioEvents: true), now);
        Assert.True(selected.Add(new(now, "audio-" + endpoint))); Assert.Empty(selected.Session.Objects);
        var unselected = new UsageRecorder(new(5, false, false, [], SystemEvents: true), now);
        Assert.False(unselected.Add(new(now, "audio-" + endpoint))); Assert.Empty(unselected.Session.Events);
        Assert.DoesNotContain(new AudioPlugin().Combinations, c => c.ActionId == HotkeyCapabilities.Send);
    }
    [Theory]
    [InlineData("all")]
    [InlineData("device-id")]
    [InlineData("")]
    public void UnknownRolesAndDeviceIdentityInputsAreRejected(string endpoint) => Assert.Throws<ArgumentException>(() => AudioCapabilities.Validate(
        new(AudioCapabilities.Changed, 1, ImmutableDictionary<string, string>.Empty.Add("endpoint", endpoint))));
}
