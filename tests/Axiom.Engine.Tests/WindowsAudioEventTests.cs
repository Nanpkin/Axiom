using System.Runtime.Versioning;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;
using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
[Collection("Native audio observers")]
public sealed class WindowsAudioEventTests
{
    [Fact(Timeout = 20000)]
    public async Task NativeAudioCallbackRegistrationIsSharedAndReleasesOnlyItsOwnLease()
    {
        await Task.Run(() =>
        {
        Assert.Equal(0, WindowsAudioEvents.ActiveConsumers);
        using (var one = new WindowsAudioEvents())
        {
            using var two = new WindowsAudioEvents(); Assert.Equal(2, WindowsAudioEvents.ActiveConsumers);
            two.Dispose(); Assert.Equal(1, WindowsAudioEvents.ActiveConsumers); Assert.False(one.Events.Completion.IsCompleted);
        }
        Assert.Equal(0, WindowsAudioEvents.ActiveConsumers);
        using (var again = new WindowsAudioEvents()) Assert.Equal(1, WindowsAudioEvents.ActiveConsumers);
        Assert.Equal(0, WindowsAudioEvents.ActiveConsumers);
        });
    }
    [Fact(Timeout = 10000)]
    public async Task UnavailableAudioBecomesUnknownWithoutEndingTheSharedProviderStream()
    {
        var provider = new AudioCapabilityProvider(() => throw new IOException("synthetic unavailable audio service"));
        using var lifetime = new CancellationTokenSource();
        var updates = System.Threading.Channels.Channel.CreateBounded<ProviderUpdate>(4);
        var run = provider.ObserveAsync([new(AudioCapabilities.Changed, "output")], update => Assert.True(updates.Writer.TryWrite(update)), lifetime.Token);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Snapshot);
        Assert.Equal(StateQuality.Unknown, Assert.Single(initial.Samples).Quality); Assert.Contains("音频通知不可用", initial.Samples[0].Reason);
        Assert.False(run.IsCompleted); lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
    [Fact]
    public void SyntheticCallbackSelectsOnlySupportedRolesAndNeverDereferencesIdentityPointers()
    {
        var received = new List<string>(); var sink = new WindowsAudioEvents.NotificationSink(received.Add);
        sink.OnDeviceAdded(1); sink.OnDeviceRemoved(1); sink.OnDeviceStateChanged(1, 1); sink.OnPropertyValueChanged(1, default);
        sink.OnDefaultDeviceChanged(0, 1, 1); sink.OnDefaultDeviceChanged(0, 2, 1); sink.OnDefaultDeviceChanged(1, 0, 1);
        Assert.Empty(received);
        sink.OnDefaultDeviceChanged(0, 0, 1); sink.OnDefaultDeviceChanged(1, 2, 1); sink.OnDefaultDeviceChanged(0, 0, 0);
        Assert.Equal(new[] { "output", "input", "output" }, received);
    }
}
