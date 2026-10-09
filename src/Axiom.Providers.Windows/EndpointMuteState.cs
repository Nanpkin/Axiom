using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record EndpointMuteSample(string Endpoint, bool? Muted, bool Baseline, string? Reason = null);

public interface IEndpointMuteSource : IDisposable
{
    ChannelReader<EndpointMuteSample> Events { get; }
    bool Failed => false;
}

internal sealed record EndpointMuteSignal(string Kind, string Endpoint = "", long Generation = 0);

// The failure callback only invalidates consumer queues. Native reads and cleanup remain on the MTA worker.
internal sealed class EndpointMuteNotifications(Action failedNotification)
{
    internal readonly object Gate = new();
    internal readonly EndpointMuteDefaultChanges DefaultChanges = new();
    private readonly HashSet<EndpointMuteSignal> pending = [];
    private readonly Channel<EndpointMuteSignal> queue = Channel.CreateBounded<EndpointMuteSignal>(64);
    private volatile bool failed;
    internal bool Failed => failed;
    internal ChannelReader<EndpointMuteSignal> Events => queue.Reader;
    internal void Enqueue(EndpointMuteSignal signal)
    {
        lock (Gate)
        {
            if (failed) return;
            if (signal.Kind == "default") DefaultChanges.Note(signal.Endpoint);
            if (!pending.Add(signal)) return;
            if (queue.Writer.TryWrite(signal)) return;
            pending.Remove(signal); failed = true;
            queue.Writer.TryComplete(new IOException("静音通知队列不可用。"));
        }
        failedNotification(); // Never wait for a blocked COM read before marking consumers Unknown.
    }
    internal bool TryRead(out EndpointMuteSignal? signal)
    {
        lock (Gate)
        {
            if (!queue.Reader.TryRead(out signal)) return false;
            pending.Remove(signal); return true;
        }
    }
    internal void Complete() => queue.Writer.TryComplete();
}

// All transitions run on the observer's single MTA thread. Generations fence old endpoint callbacks.
public sealed class EndpointMuteState
{
    private readonly string endpoint;
    private bool? muted;
    private bool seeded;
    public long Generation { get; private set; }
    public EndpointMuteState(string endpoint)
    {
        if (endpoint is not ("input" or "output")) throw new ArgumentException("默认设备类型无效。");
        this.endpoint = endpoint;
    }
    public EndpointMuteSample Reset(string reason)
    {
        Generation++; muted = null; seeded = false;
        return new(endpoint, null, false, reason);
    }
    public EndpointMuteSample? Read(long generation, bool value)
    {
        if (generation != Generation) return null;
        if (seeded && muted == value) return null;
        var baseline = !seeded; seeded = true; muted = value;
        return new(endpoint, value, baseline);
    }
}

// Notification counts retain a default-role change even when the worker coalesces A -> B -> A into one signal.
internal sealed class EndpointMuteDefaultChanges
{
    private long input;
    private long output;
    internal void Note(string endpoint)
    {
        if (endpoint == "input") input++;
        else if (endpoint == "output") output++;
    }
    internal long Version(string endpoint) => endpoint == "input" ? input : output;
    internal static bool RequiresRebind(string? currentIdentity, string candidateIdentity, long boundVersion, long latestVersion) =>
        currentIdentity != candidateIdentity || boundVersion != latestVersion;
}
