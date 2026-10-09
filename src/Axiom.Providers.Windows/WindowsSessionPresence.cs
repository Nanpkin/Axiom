using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionPresence : ISessionPresenceSource
{
    private readonly WindowsSessionEvents source = new(presenceOnly: true);
    public ChannelReader<string> Events => source.Events;
    public bool Failed => source.Failed;
    public void Dispose() => source.Dispose();
}
