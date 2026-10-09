using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// Contains only public category enum values. null or incomplete enumeration is a possible connected unknown network.
public sealed record NetworkCategorySnapshot(int?[] Categories, bool Complete = true);
public sealed record NetworkCategorySample(bool?[] Matches, bool[] Baselines, string? Reason = null)
{
    public static int Index(string category) => category switch { "public" => 0, "private" => 1, "domain" => 2, _ => throw new ArgumentException("网络分类资源无效。") };
}
public interface INetworkCategorySource : IDisposable
{
    ChannelReader<NetworkCategorySample> Events { get; }
    bool Failed { get; }
}
public sealed class NetworkCategoryState
{
    private bool seeded;
    private bool?[] previous = [null, null, null];
    public static bool? Evaluate(NetworkCategorySnapshot snapshot, int category)
    {
        if (category is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(category));
        if (snapshot.Categories.Contains(category)) return true;
        return snapshot.Complete && snapshot.Categories.All(c => c is >= 0 and <= 2) ? false : null;
    }
    public NetworkCategorySample? Read(NetworkCategorySnapshot snapshot)
    {
        var values = Enumerable.Range(0, 3).Select(c => Evaluate(snapshot, c)).ToArray();
        if (seeded && values.SequenceEqual(previous)) return null;
        var baselines = Enumerable.Range(0, 3).Select(c => !seeded || previous[c] is null && values[c].HasValue).ToArray();
        seeded = true; previous = values;
        return new(values, baselines, values.Any(v => v is null) ? "部分网络分类未知；不把无法查询当作非匹配。" : null);
    }
}

// Documented netlistmgr.idl INetworkEvents order. IDs are accepted only by the callback ABI, never retained.
[ComVisible(true), Guid("DCB00004-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INetworkCategoryCallback
{
    [PreserveSig] int NetworkAdded(Guid networkId);
    [PreserveSig] int NetworkDeleted(Guid networkId);
    [PreserveSig] int NetworkConnectivityChanged(Guid networkId, uint connectivity);
    [PreserveSig] int NetworkPropertyChanged(Guid networkId, uint flags);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class NetworkCategoryCallbackSink(Action hint) : INetworkCategoryCallback
{
    public int NetworkAdded(Guid networkId) { hint(); return 0; }
    public int NetworkDeleted(Guid networkId) { hint(); return 0; }
    public int NetworkConnectivityChanged(Guid networkId, uint connectivity) { hint(); return 0; }
    public int NetworkPropertyChanged(Guid networkId, uint flags) { if ((flags & 0x11u) != 0) hint(); return 0; }
}
public sealed class NetworkCategoryNotifications(Action? failure = null)
{
    private readonly Channel<byte> queue = Channel.CreateBounded<byte>(64);
    private long version;
    private volatile bool failed;
    public ChannelReader<byte> Events => queue.Reader;
    public long Version => Interlocked.Read(ref version);
    public bool Failed => failed;
    public void Hint()
    {
        Interlocked.Increment(ref version);
        if (queue.Writer.TryWrite(1)) return;
        failed = true; queue.Writer.TryComplete(new IOException("网络分类通知积压，检测未知。")); failure?.Invoke();
    }
    public void Complete() => queue.Writer.TryComplete();
}
