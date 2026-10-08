using Axiom.Contracts;

namespace Axiom.Infrastructure;

public sealed record SteamToolScan(ProcessGroupEntry[] Processes, int InstalledGames, string[] Warnings, bool Complete);
public sealed record SteamToolState(SteamToolSettings Settings, string? GroupId, DateTimeOffset? LastScanUtc,
    DateTimeOffset? LastSuccessUtc, int InstalledGames, ProcessGroupEntry[] Processes, string[] Warnings, string? Error, string ScanId, bool HasCompleteSnapshot = false)
{
    public static SteamToolState Empty => new(new(0, false, []), null, null, null, 0, [], [], null, Guid.NewGuid().ToString("N"));
}

// Coordinates the low-frequency tool and group writes; scanning never blocks rule execution or holds SQLite open.
public sealed class SteamToolManager(SqliteProcessGroupStore store,
    Func<bool, CancellationToken, Task<SteamToolScan>> scan, Action<string, string, bool>? record = null) : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private bool scanning;
    public ProcessGroup[] ReadGroups()
    {
        lock (gate) { var state = store.ReadSteamTool(); return [.. store.Read().Select(g => g with { AutoManaged = g.Id == state.GroupId })]; }
    }
    public void SaveGroup(ProcessGroup group)
    {
        lock (gate)
        {
            if (group.Id == store.ReadSteamTool().GroupId || group.Name.Trim().Equals(SteamTool.GroupName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("这个组由 Steam 工具自动维护，请在工具设置中调整排除项。");
            store.Save(group);
        }
    }
    public void DeleteGroup(string id, long version)
    {
        lock (gate)
        {
            if (id == store.ReadSteamTool().GroupId) throw new InvalidOperationException("这个组由 Steam 工具维护；可在工具箱关闭自动更新，名单会保留。");
            store.Delete(id, version);
        }
    }
    public SteamToolSnapshot Read(int offset = 0, string? expectedScanId = null)
    {
        lock (gate)
        {
            var state = store.ReadSteamTool();
            if (offset < 0 || offset > state.Processes.Length || offset > 0 && expectedScanId != state.ScanId)
                throw new InvalidOperationException("Steam 名单已更新，请重新打开工具设置。");
            var next = offset + Math.Min(SteamTool.PageSize, state.Processes.Length - offset);
            var group = store.Read().SingleOrDefault(g => g.Id == state.GroupId);
            return new(state.Settings, state.GroupId, state.LastScanUtc, state.LastSuccessUtc, scanning, state.InstalledGames,
                state.Processes.Length, group?.Entries.Length ?? 0, state.Error, state.Warnings, state.ScanId,
                state.Processes[offset..next], next < state.Processes.Length ? next : null);
        }
    }
    public async Task<SteamToolSnapshot> SaveAsync(SteamToolSettings settings, CancellationToken token)
    {
        settings = SteamTool.Normalize(settings); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var state = store.ReadSteamTool();
            if (state.Settings.Version != settings.Version) throw new InvalidOperationException("Steam 设置已在别处更改，请重新打开后编辑。");
            state = state with { Settings = settings with { Version = checked(settings.Version + 1) } };
            // Apply exclusions to the last complete snapshot immediately. A subsequent scan may discover new games.
            if (state.Settings.Enabled && state.HasCompleteSnapshot) state = Apply(state, state.Processes, state.InstalledGames, state.Warnings, state.LastScanUtc!.Value);
            else store.SaveSteamTool(state);
        }
        record?.Invoke("Steam 工具设置已保存", settings.Enabled ? "每次 Axiom 后台启动检查游戏，排除项已保存。" : "自动更新已关闭，现有进程组保留。", true);
        if (settings.Enabled) await ScanAsync(false, true, token);
        return Read();
    }
    public async Task<SteamToolSnapshot> ScanAsync(bool refresh, bool requireEnabled, CancellationToken token)
    {
        await scanGate.WaitAsync(token);
        try
        {
            lock (gate) { if (requireEnabled && !store.ReadSteamTool().Settings.Enabled) return Read(); scanning = true; }
            try
            {
                var result = await scan(refresh, token); token.ThrowIfCancellationRequested();
                if (result.Processes.Length > 4096) throw new InvalidOperationException("扫描程序数量超过当前支持的 4096 个，已保留原组。");
                lock (gate)
                {
                    var state = store.ReadSteamTool();
                    if (!result.Complete) throw new InvalidOperationException("Steam 元数据或游戏库暂不可用，本次结果不完整，已保留原组。" + string.Join(" ", result.Warnings));
                    var entries = result.Processes.DistinctBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
                    state = state with { LastScanUtc = DateTimeOffset.UtcNow, InstalledGames = result.InstalledGames,
                        Processes = entries, Warnings = result.Warnings, Error = null, ScanId = Guid.NewGuid().ToString("N"), HasCompleteSnapshot = true };
                    if (state.Settings.Enabled) Apply(state, entries, result.InstalledGames, result.Warnings, state.LastScanUtc.Value);
                    else store.SaveSteamTool(state);
                }
                var snapshot = Read();
                record?.Invoke(snapshot.Error is not null ? "Steam 游戏组更新未完成" : snapshot.Settings.Enabled ? "Steam 游戏组已更新" : "Steam 扫描已完成",
                    $"发现 {snapshot.InstalledGames} 个安装项目、{snapshot.DetectedCount} 个程序；" +
                    (snapshot.Error ?? (snapshot.Settings.Enabled ? $"已同步 {snapshot.ExportedCount} 个程序到“{SteamTool.GroupName}”。" : "自动更新已关闭，未修改进程组。")), snapshot.Error is null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                lock (gate)
                {
                    var state = store.ReadSteamTool(); store.SaveSteamTool(state with { LastScanUtc = DateTimeOffset.UtcNow, Error = error.Message[..Math.Min(error.Message.Length, 2000)] });
                }
                record?.Invoke("Steam 游戏组更新未完成", error.Message + " 原进程组已保留。", false);
            }
        }
        finally { lock (gate) scanning = false; scanGate.Release(); }
        return Read();
    }
    private SteamToolState Apply(SteamToolState state, ProcessGroupEntry[] entries, int installedGames, string[] warnings, DateTimeOffset at)
    {
        var selected = entries.Where(e => !state.Settings.ExcludedProcesses.Contains(e.ProcessName, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (selected.Length > ProcessGroups.MaximumEntries)
        {
            state = state with { Error = $"发现 {selected.Length} 个未排除程序，当前每组最多 128 个；请增加排除项，原组已保留。" };
            store.SaveSteamTool(state); return state;
        }
        var existing = store.Read().SingleOrDefault(g => g.Id == state.GroupId) ??
            (state.GroupId is null ? store.Read().SingleOrDefault(g => g.Name.Equals(SteamTool.GroupName, StringComparison.OrdinalIgnoreCase)) : null);
        var group = new ProcessGroup(existing?.Id ?? state.GroupId ?? Guid.NewGuid().ToString("D"), existing?.Version ?? 0, SteamTool.GroupName, selected);
        state = state with { GroupId = group.Id, LastSuccessUtc = at, InstalledGames = installedGames, Processes = entries, Warnings = warnings, Error = null };
        // Unchanged snapshots do not increase the group's version or dirty an editor elsewhere.
        try { store.SaveSteamTool(state, existing is not null && existing.Name == group.Name && existing.Entries.SequenceEqual(group.Entries) ? null : group); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            state = state with { LastSuccessUtc = store.ReadSteamTool().LastSuccessUtc, GroupId = store.ReadSteamTool().GroupId,
                Error = error.Message + " 原组已保留，请调整排除项或清理其他组。" };
            store.SaveSteamTool(state);
        }
        return state;
    }
    public void Dispose() => scanGate.Dispose();
}
