using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record InternetConnectivitySample(bool? Available, bool Baseline, string? Reason = null);
public interface IInternetConnectivitySource : IDisposable
{
    ChannelReader<InternetConnectivitySample> Events { get; }
    bool Failed => false;
}

public sealed class InternetConnectivityState
{
    private bool seeded;
    private bool? previous;
    // Public NLM_CONNECTIVITY values: no traffic, subnet, local network and IPv4/IPv6 Internet.
    public static bool? Evaluate(uint? flags) => flags.HasValue && (flags.Value & ~0x773u) == 0 ? (flags.Value & 0x440u) != 0 : null;
    public InternetConnectivitySample? Read(uint? flags, string? reason = null)
    {
        var available = Evaluate(flags);
        if (seeded && available == previous) return null;
        var baseline = !seeded || (!previous.HasValue && available.HasValue);
        seeded = true; previous = available;
        return new(available, baseline, available.HasValue ? null : reason ?? "Windows未能确认Internet状态；不会当作不可用。");
    }
}

// The callback interface is public and COM-visible; its sole IUnknown method matches the Windows SDK IDL.
[ComVisible(true), Guid("DCB00001-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInternetConnectivityCallback
{
    [PreserveSig] int ConnectivityChanged(uint connectivity);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class InternetConnectivityCallbackSink(Action hint) : IInternetConnectivityCallback
{
    public int ConnectivityChanged(uint connectivity) { hint(); return 0; } // Ignore payload; query current state on the MTA consumer.
}

public sealed class InternetConnectivityNotifications(Action? failedNotification = null)
{
    private readonly Channel<byte> queue = Channel.CreateBounded<byte>(64);
    private volatile bool failed;
    public ChannelReader<byte> Events => queue.Reader;
    public bool Failed => failed;
    public void Hint()
    {
        if (queue.Writer.TryWrite(1)) return;
        failed = true; queue.Writer.TryComplete(new IOException("Internet状态通知积压，检测状态未知。"));
        failedNotification?.Invoke(); // Fault signal only; never query COM or run provider work on a native callback.
    }
    public void Complete() => queue.Writer.TryComplete();
}
