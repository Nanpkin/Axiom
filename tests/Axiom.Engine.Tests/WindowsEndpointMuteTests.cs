using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
[Collection("Native audio observers")]
public sealed class WindowsEndpointMuteTests
{
    [Fact]
    public void NativeCallbackCanQueryItsPublicComInterfaceAndInvokeTheCorrectVtableSlot()
    {
        var calls = 0; var callback = new EndpointMuteCallbackSink(() => calls++);
        var unknown = Marshal.GetIUnknownForObject(callback); nint requested = 0;
        try
        {
            var iid = new Guid("657804FA-D6AD-4496-8A60-352752AF4F89");
            Assert.Equal(0, Marshal.QueryInterface(unknown, in iid, out requested));
            var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(requested), 3 * IntPtr.Size);
            var notify = Marshal.GetDelegateForFunctionPointer<Notify>(function);
            Assert.Equal(0, notify(requested, 1)); Assert.Equal(1, calls); // Invalid data pointer must never be read.
        }
        finally { if (requested != 0) Marshal.Release(requested); Marshal.Release(unknown); GC.KeepAlive(callback); }
    }

    [Fact]
    public void EndpointInventoryNotificationsAreOptInAndNeverReadDevicePointers()
    {
        var selected = new List<string>(); var inventory = 0;
        var notification = new WindowsAudioEvents.NotificationSink(selected.Add, () => inventory++);
        notification.OnDeviceAdded(1); notification.OnDeviceRemoved(1); notification.OnDeviceStateChanged(1, 0);
        notification.OnPropertyValueChanged(1, default); Assert.Equal(3, inventory); Assert.Empty(selected);
        notification.OnDefaultDeviceChanged(1, 2, 1); Assert.Equal(new[] { "input" }, selected);
        var ordinary = new WindowsAudioEvents.NotificationSink(selected.Add);
        ordinary.OnDeviceAdded(1); ordinary.OnDeviceRemoved(1); ordinary.OnDeviceStateChanged(1, 1);
        Assert.Single(selected);
    }

    [Fact(Timeout = 20000)]
    public async Task NativeObserversShareOneDefaultNotificationSubscriptionAndReleaseOnlyTheirOwnConsumers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var existing = new WindowsAudioEvents(); var previous = WindowsAudioEvents.ActiveConsumers;
        var one = new WindowsEndpointMuteEvents(["input"]);
        var two = new WindowsEndpointMuteEvents(["input", "output"]);
        try
        {
            // No microphone or speaker must exist. Known or a stable no-device Unknown both establish native initialization.
            await foreach (var sample in one.Events.ReadAllAsync(deadline.Token))
                if (sample.Muted.HasValue || sample.Reason == "默认设备不可用或静音状态无法读取。") break;
            Assert.Equal(2, WindowsEndpointMuteEvents.ActiveConsumers);
            Assert.Equal(previous + 1, WindowsAudioEvents.ActiveConsumers);
            two.Dispose(); Assert.Equal(1, WindowsEndpointMuteEvents.ActiveConsumers); Assert.False(one.Events.Completion.IsCompleted);
            one.Dispose(); Assert.Equal(0, WindowsEndpointMuteEvents.ActiveConsumers);
            Assert.Equal(previous, WindowsAudioEvents.ActiveConsumers); Assert.False(existing.Events.Completion.IsCompleted);
        }
        finally { two.Dispose(); one.Dispose(); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Notify(nint self, nint notificationData);
}
