using Axiom.Contracts;
using Axiom.ServiceHost;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Axiom.Engine.Tests;

[SupportedOSPlatform("windows")]
public sealed class LocalUserPipeTests
{
    [Fact(Timeout = 5000)]
    public async Task ExplicitUserOwnershipWorksForElevatedAndNormalClients()
    {
        var name = "axiom-user-owner-test-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        using var server = LocalPipeFactory.Create(name, identity.User!.Value);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        using var client = LocalUserPipe.Create(name);
        await client.ConnectForUserAsync(2000, deadline.Token);
        await accepting;
        server.WriteByte(17);
        Assert.Equal(17, client.ReadByte());
    }
}
