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
    private static readonly Func<string, int?, string>[] SessionEndpoints =
    [
        AutomationProtocol.PipeName, ProviderProtocol.PipeName,
        GameHdrProtocol.HostPipe, GameHdrProtocol.AgentPipe,
        KeyboardShortcuts.AgentPipe, ServiceStatusProtocol.PipeName, RuleDraftProtocol.PipeName
    ];

    [Fact]
    public void EveryEndpointSeparatesUsersSessionsAndChannelsAndDefaultsToOsSession()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        const string user = "S-1-5-21-1001";
        var first = SessionEndpoints.Select(name => name(user, 1)).ToArray();
        var second = SessionEndpoints.Select(name => name(user, 2)).ToArray();
        var otherUser = SessionEndpoints.Select(name => name("S-1-5-21-1002", 1)).ToArray();
        Assert.Equal(21, first.Concat(second).Concat(otherUser).Distinct(StringComparer.Ordinal).Count());
        foreach (var name in SessionEndpoints)
        {
            Assert.Equal(name(user, process.SessionId), name(user, null));
            Assert.EndsWith("-s1", name(user, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => name(user, -1));
        }
    }

    [Fact(Timeout = 15000)]
    public async Task ConcurrentSessionEndpointsRouteOnlyToTheirOwnListenerWithoutLegacyFallback()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!.Value;
        var user = "routing-test-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var name in SessionEndpoints)
        {
            using var first = LocalPipeFactory.Create(name(user, 1), owner);
            using var second = LocalPipeFactory.Create(name(user, 2), owner);
            // An old unsuffixed listener must not receive either connection.
            using var legacy = LocalPipeFactory.Create(name(user, 1)[..^3], owner);
            var firstAccepted = first.WaitForConnectionAsync(deadline.Token);
            var secondAccepted = second.WaitForConnectionAsync(deadline.Token);
            using var firstClient = LocalUserPipe.Create(name(user, 1));
            using var secondClient = LocalUserPipe.Create(name(user, 2));
            await firstClient.ConnectForUserAsync(2000, deadline.Token);
            await secondClient.ConnectForUserAsync(2000, deadline.Token);
            await Task.WhenAll(firstAccepted, secondAccepted);
            await first.WriteAsync(new byte[] { 17 }, deadline.Token);
            await second.WriteAsync(new byte[] { 29 }, deadline.Token);
            var marker = new byte[1];
            await firstClient.ReadExactlyAsync(marker, deadline.Token);
            Assert.Equal(17, marker[0]);
            await secondClient.ReadExactlyAsync(marker, deadline.Token);
            Assert.Equal(29, marker[0]);
            Assert.False(legacy.IsConnected);
        }
    }
    [Fact(Timeout = 15000)]
    public async Task FullAutomationCapacityKeepsAListenerAndAcceptsAgainAfterAClientCloses()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!.Value;
        var name = AutomationProtocol.PipeName("capacity-test-" + Guid.NewGuid().ToString("N"));
        const int maximumClients = 8;
        var servers = new List<System.IO.Pipes.NamedPipeServerStream>();
        var clients = new List<System.IO.Pipes.NamedPipeClientStream>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            for (var i = 0; i < maximumClients; i++)
            {
                var server = LocalPipeFactory.Create(name, owner, LocalPipeFactory.AutomationMaximumInstances); servers.Add(server);
                var accepted = server.WaitForConnectionAsync(deadline.Token);
                var client = LocalUserPipe.Create(name); clients.Add(client);
                await client.ConnectForUserAsync(2000, deadline.Token);
                await accepted;
            }
            // Even with eight live clients the accept loop needs one native instance
            // to accept and close an excess connection without losing the listener.
            using (var overflow = LocalPipeFactory.Create(name, owner, LocalPipeFactory.AutomationMaximumInstances))
            using (var extra = LocalUserPipe.Create(name))
            {
                var accepted = overflow.WaitForConnectionAsync(deadline.Token);
                await extra.ConnectForUserAsync(2000, deadline.Token);
                await accepted;
                overflow.Dispose();
                Assert.Equal(0, await extra.ReadAsync(new byte[1], deadline.Token));
            }
            clients[0].Dispose(); servers[0].Dispose();
            using var replacement = LocalPipeFactory.Create(name, owner, LocalPipeFactory.AutomationMaximumInstances);
            var ready = replacement.WaitForConnectionAsync(deadline.Token);
            using var next = LocalUserPipe.Create(name);
            await next.ConnectForUserAsync(2000, deadline.Token);
            await ready;
            await replacement.WriteAsync(new byte[] { 37 }, deadline.Token);
            var marker = new byte[1];
            await next.ReadExactlyAsync(marker, deadline.Token);
            Assert.Equal(37, marker[0]);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            foreach (var server in servers) server.Dispose();
        }
    }
}
