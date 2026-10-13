using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsSessionLockState : ISessionLockStateSource
{
    private readonly WindowsSessionEvents source = new(lockStateOnly: true); // subscribe before WTS reads
    public ChannelReader<string> Events => source.Events;
    public bool Failed => source.Failed;
    public bool? Read(CancellationToken token) => WindowsSessionLock.ReadLocked(token);
    public void Dispose() => source.Dispose();
}
