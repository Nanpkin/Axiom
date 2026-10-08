using System.Buffers.Binary;
using System.Text;
using Axiom.Contracts;
using Axiom.Launcher;

namespace Axiom.Engine.Tests;

public sealed class DistributionTests
{
    [Fact]
    public void SharedLayoutAndLegacyLayoutResolveTheSameRootAndRequireAllEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "axiom-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var legacy = AxiomInstallLayout.At(root);
            Assert.EndsWith(Path.Combine("backend", "host", "Axiom.ServiceHost.exe"), legacy.Host);
            Assert.Equal(root, AxiomInstallLayout.ForComponent(Path.GetDirectoryName(legacy.Host)!).Root);
            Directory.CreateDirectory(Path.Combine(root, "app"));
            var shared = AxiomInstallLayout.At(root);
            Assert.Equal(shared, AxiomInstallLayout.ForComponent(Path.Combine(root, "app") + Path.DirectorySeparatorChar));
            Assert.False(shared.Complete);
            foreach (var path in new[] { shared.Launcher, shared.Host, shared.Agent, shared.Ui }) File.WriteAllText(path, "fixture");
            Assert.True(shared.Complete);
            File.Delete(shared.Ui); Assert.False(shared.Complete);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void UninstallOnlyOwnsTheExactLauncherAndBackgroundAction()
    {
        const string xml = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>C:\\Axiom\\Axiom.exe</Command><Arguments>--background</Arguments></Exec></Actions></Task>";
        Assert.True(LoginTaskCleanup.Owns(xml, @"C:\Axiom\Axiom.exe"));
        Assert.False(LoginTaskCleanup.Owns(xml, @"C:\Other\Axiom.exe"));
        Assert.False(LoginTaskCleanup.Owns(xml.Replace("--background", "--different"), @"C:\Axiom\Axiom.exe"));
        Assert.False(LoginTaskCleanup.Owns(xml.Replace("</Actions>", "<Exec /></Actions>"), @"C:\Axiom\Axiom.exe"));
    }
    [Fact]
    public async Task NativeLauncherWireRoundTripsStatusAndStillRejectsUnknownFields()
    {
        var message = new GameHdrMessage(1, "status", "distribution", Status: new("ready", "原因", DateTimeOffset.UtcNow, true, [], []));
        using var stream = new MemoryStream();
        await GameHdrProtocol.WriteAsync(stream, message, default); stream.Position = 0;
        var read = await GameHdrProtocol.ReadAsync(stream, default);
        Assert.Equal(message.RequestId, read!.RequestId); Assert.Equal(message.Status!.Reason, read.Status!.Reason); Assert.True(read.Status.AgentConnected);
        var bytes = Encoding.UTF8.GetBytes("{\"Version\":1,\"Type\":\"status\",\"RequestId\":\"test\",\"Unexpected\":true}");
        using var invalid = new MemoryStream(); var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        invalid.Write(prefix); invalid.Write(bytes); invalid.Position = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => GameHdrProtocol.ReadAsync(invalid, default));
    }
}
