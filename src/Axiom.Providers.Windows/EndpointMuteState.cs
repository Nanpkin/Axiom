using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record EndpointMuteSample(string Endpoint, bool? Muted, bool Baseline, string? Reason = null);

public interface IEndpointMuteSource : IDisposable
{
    ChannelReader<EndpointMuteSample> Events { get; }
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
