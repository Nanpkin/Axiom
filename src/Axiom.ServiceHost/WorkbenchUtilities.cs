using Axiom.Contracts;
using Axiom.Infrastructure;
using Microsoft.Win32;

namespace Axiom.ServiceHost;

internal sealed class WorkbenchUtilities : IAsyncDisposable
{
    private readonly SteamCatalog steam;
    private readonly SteamToolManager tool;
    private readonly CancellationTokenSource lifetime = new();
    private Task? startupScan;
    public WorkbenchUtilities(SqliteProcessGroupStore groups, SteamCatalog steam, Action<string, string, bool> record)
    { this.steam = steam; tool = new(groups, steam.ReadAllAsync, record); }
    public void StartStartupScan() => startupScan ??= Task.Run(() => tool.ScanAsync(true, true, lifetime.Token), lifetime.Token);
    public async Task<AutomationMessage> HandleAsync(AutomationMessage request, CancellationToken token)
    {
        if (request.Type == "scan-steam") return new(1, "steam", request.RequestId,
            Steam: await steam.ReadAsync(request.RefreshSteam == true, request.SteamOffset ?? 0, request.SteamScanId, token));
        if (request.Type == "read-steam-tool") return new(1, "steam-tool", request.RequestId, SteamTool: tool.Read(request.SteamOffset ?? 0, request.SteamScanId));
        if (request.Type == "save-steam-settings") return new(1, "steam-tool", request.RequestId, SteamTool: await tool.SaveAsync(request.SteamSettings!, token));
        if (request.Type == "refresh-steam-tool") return new(1, "steam-tool", request.RequestId, SteamTool: await tool.ScanAsync(true, false, token));
        if (request.Type == "save-group") tool.SaveGroup(request.Group!);
        else if (request.Type == "delete-group") tool.DeleteGroup(request.Id!, request.ExpectedVersion!.Value);
        return new(1, "groups", request.RequestId, Groups: tool.ReadGroups());
    }
    public static string? LocateSteam()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var path = user?.GetValue("SteamPath") as string;
        if (!string.IsNullOrWhiteSpace(path)) return path;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("InstallPath") is string install && !string.IsNullOrWhiteSpace(install)) return install;
        }
        return null;
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (startupScan is not null) try { await startupScan; } catch (OperationCanceledException) { }
        tool.Dispose(); lifetime.Dispose();
    }
}
