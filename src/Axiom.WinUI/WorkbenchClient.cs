using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;

namespace Axiom.WinUI;

internal static class WorkbenchClient
{
    public static async Task<AutomationMessage> RequestAsync(AutomationMessage request, CancellationToken token = default)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException();
        using var pipe = new NamedPipeClientStream(".", AutomationProtocol.PipeName(sid), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(13));
        await pipe.ConnectForUserAsync(3000, timeout.Token);
        await AutomationProtocol.WriteAsync(pipe, request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(12) }, timeout.Token);
        var response = await AutomationProtocol.ReadAsync(pipe, timeout.Token);
        if (response.Version != 1 || response.RequestId != request.RequestId) throw new InvalidDataException();
        return response;
    }
}
