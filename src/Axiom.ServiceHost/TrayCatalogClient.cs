using Axiom.Contracts;

namespace Axiom.ServiceHost;

internal static class TrayCatalogClient
{
    public static async Task<TrayCatalog> ReadAsync(string sid, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = LocalUserPipe.Create(ProviderProtocol.PipeName(sid));
        await pipe.ConnectForUserAsync(3000, timeout.Token);
        var id = Guid.NewGuid().ToString("N");
        await ProviderProtocol.WriteAsync(pipe, new(1, "read-tray", id, DateTimeOffset.UtcNow.AddSeconds(10)), timeout.Token);
        var response = await ProviderProtocol.ReadAsync(pipe, timeout.Token);
        if (response.Version != 1 || response.RequestId != id || response.Type != "tray" || response.Tray is null)
            throw new IOException(response.Reason ?? "托盘名单读取失败，请确认会话后台已运行。");
        TrayCatalogValidation.Validate(response.Tray);
        return response.Tray;
    }
}
