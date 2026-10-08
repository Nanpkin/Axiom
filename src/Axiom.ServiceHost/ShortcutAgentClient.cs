using System.IO.Pipes;
using Axiom.Contracts;

namespace Axiom.ServiceHost;

internal sealed class ShortcutAgentClient(string sid) : IShortcutAgentClient
{
    public async Task<ShortcutSnapshot> RequestAsync(string operation, ShortcutBinding[]? bindings, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", KeyboardShortcuts.AgentPipe(sid), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectForUserAsync(3000, timeout.Token); var id = Guid.NewGuid().ToString("N");
        await AutomationProtocol.WriteAsync(pipe, new(1, operation, id, DateTimeOffset.UtcNow.AddSeconds(10), Bindings: bindings), timeout.Token);
        var response = await AutomationProtocol.ReadAsync(pipe, timeout.Token);
        if (response.Version != 1 || response.RequestId != id) throw new IOException("快捷键后台结果未确认。");
        if (response.Type != "shortcuts" || response.Shortcuts is null) throw new InvalidOperationException(response.Error ?? "快捷键未生效，请重试。");
        return response.Shortcuts;
    }
}
