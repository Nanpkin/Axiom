using System.Collections.Immutable;
using System.Text;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.SessionAgent;
using Xunit;

namespace Axiom.Engine.Tests;

public sealed class SunshineTests
{
    [Theory]
    [InlineData(SunshineCapabilities.Connected)]
    [InlineData(SunshineCapabilities.Resumable)]
    [InlineData(SunshineCapabilities.Either)]
    public void BuiltInDetectionCompilesEachChoiceToAnObservedCondition(string state)
    {
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<SunshinePlugin>());
        var detection = Assert.Single(plugin.Detections);
        Assert.True(detection.Descriptor.IsStateCondition);
        var invocation = new TaskInvocation(SunshineCapabilities.State, 1,
            ImmutableDictionary<string, string>.Empty.Add("state", state));
        var condition = detection.Compile(invocation, ResourceScope.Machine);
        Assert.Equal(state, Assert.Single(condition.Dependencies).ResourceId);
        Assert.Contains(new(SunshineCapabilities.State, NotificationCapabilities.Show), plugin.Combinations);
    }

    [Fact]
    public async Task PushSnapshotsDistinguishStreamingPausedIdleAndObscuredStatus()
    {
        var resources = new[] { SunshineCapabilities.Connected, SunshineCapabilities.Resumable, SunshineCapabilities.Either }
            .Select(state => new ProviderResource(SunshineCapabilities.State, state)).ToArray();
        static string Event(int revision, string status) =>
            $"event: tray-state\ndata: {{\"protocol_version\":1,\"revision\":{revision},\"status\":\"{status}\"}}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Event(1, "streaming") + ": tray-keepalive\n\n" +
            Event(2, "paused") + Event(1, "streaming") + Event(3, "pairing") + Event(4, "idle")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var updates = new List<ProviderUpdate>();
        await SunshineCapabilityProvider.ReadEventsAsync(stream, status =>
            updates.Add(SunshineCapabilityProvider.Update(resources, status, updates.Count == 0, "unknown")), deadline);

        Assert.Equal(4, updates.Count);
        Assert.True(updates[0].Snapshot);
        Assert.Equal(new bool?[] { true, false, true }, updates[0].Samples.Select(sample => sample.Boolean));
        Assert.Equal(new bool?[] { false, true, true }, updates[1].Samples.Select(sample => sample.Boolean));
        Assert.All(updates[2].Samples, sample =>
        {
            Assert.Equal(StateQuality.Unknown, sample.Quality);
            Assert.Null(sample.Boolean);
        });
        Assert.Equal(new bool?[] { false, false, false }, updates[3].Samples.Select(sample => sample.Boolean));
    }
}
