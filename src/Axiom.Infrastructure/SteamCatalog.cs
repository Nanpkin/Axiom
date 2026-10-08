using System.Text;
using System.Text.Json;
using Axiom.Contracts;

namespace Axiom.Infrastructure;

public sealed class SteamCatalog(Func<string?> locateRoot) : IDisposable
{
    public const int PageSize = 40;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<FileSystemWatcher> watchers = [];
    private SteamGame[]? games;
    private string[] warnings = [];
    private string? root, scanId;
    private DateTimeOffset scannedAt;
    private long generation, cachedGeneration = -1;
    private volatile bool watching;
    private bool complete = true;

    public async Task<SteamCatalogPage> ReadAsync(bool refresh, int offset, string? expectedScanId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var fromCache = true;
            if (offset == 0)
            {
                var currentRoot = locateRoot();
                if (string.IsNullOrWhiteSpace(currentRoot)) throw new InvalidOperationException("未找到本机 Steam 安装位置。请先安装并运行一次 Steam。");
                currentRoot = Path.GetFullPath(currentRoot);
                if (refresh || games is null || !watching || root != currentRoot || cachedGeneration != Volatile.Read(ref generation))
                {
                    await Task.Run(() => Scan(currentRoot, token), token); fromCache = false;
                }
            }
            else if (games is null || expectedScanId != scanId) throw new InvalidOperationException("扫描结果已更新，请重新刷新。");
            if (offset < 0 || offset > games!.Length) throw new ArgumentException("扫描结果页无效。");
            var count = Math.Min(PageSize, games.Length - offset);
            while (true)
            {
                var page = games.Skip(offset).Take(count).ToArray(); var next = offset + count;
                var result = new SteamCatalogPage(scanId!, scannedAt, fromCache, page, games.Length, next < games.Length ? next : null, warnings, complete);
                if (JsonSerializer.SerializeToUtf8Bytes(result).Length <= AutomationProtocol.MaximumFrame - 4096) return result;
                if (count <= 1) throw new InvalidDataException("Steam 结果内容过大，无法传输。");
                count /= 2;
            }
        }
        finally { gate.Release(); }
    }
    private void Scan(string currentRoot, CancellationToken token)
    {
        complete = true;
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); watching = true;
        var notes = new List<string>();
        Watch(Path.Combine(currentRoot, "steamapps"), ["libraryfolders.vdf", "appmanifest_*.acf"], notes);
        Watch(Path.Combine(currentRoot, "appcache"), ["appinfo.vdf"], notes);
        var startGeneration = Volatile.Read(ref generation);
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentRoot };
        var libraryFile = Path.Combine(currentRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFile)) { complete = false; AddWarning(notes, "Steam 游戏库目录元数据暂不可用，无法确认全部库。"); }
        if (File.Exists(libraryFile))
        {
            var metadata = SteamKeyValues.Parse(ReadText(libraryFile));
            foreach (var item in (metadata.Get("libraryfolders") ?? metadata.Get("LibraryFolders") ?? metadata).Children)
            {
                if (!int.TryParse(item.Key, out _)) continue;
                var path = item.Value.Text("path") ?? item.Value.Value;
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) continue;
                libraries.Add(Path.GetFullPath(path));
                if (libraries.Count > 32) throw new InvalidDataException("Steam 库数量超过当前支持的 32 个。");
            }
        }
        foreach (var library in libraries.Where(x => !x.Equals(currentRoot, StringComparison.OrdinalIgnoreCase)))
            Watch(Path.Combine(library, "steamapps"), ["appmanifest_*.acf"], notes);
        var collected = new Dictionary<uint, SteamGame>();
        foreach (var library in libraries)
        {
            token.ThrowIfCancellationRequested(); var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) { complete = false; notes.Add("游戏库暂不可用：" + library); continue; }
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                {
                    token.ThrowIfCancellationRequested();
                    if (collected.Count >= 4096) { complete = false; notes.Add("本次最多显示 4096 个安装清单。"); break; }
                    try
                    {
                        var app = SteamKeyValues.Parse(ReadText(manifest)).Get("AppState") ?? throw new InvalidDataException("缺少 AppState。");
                        if (!uint.TryParse(app.Text("appid"), out var id) || id == 0) throw new InvalidDataException("无效的 appid。");
                        var name = app.Text("name") ?? "Steam " + id;
                        var install = SteamGameMetadata.InstallationPath(library, app.Text("installdir") ?? "");
                        if (name.Length > 200 || install.Length > 1024) throw new InvalidDataException("游戏名称或路径过长。");
                        if (!Directory.Exists(install)) { complete = false; notes.Add(name + " 的安装目录暂不可用。"); continue; }
                        long.TryParse(app.Text("SizeOnDisk"), out var size);
                        collected[id] = new(id, name, install, Math.Max(0, size), []);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                    { complete = false; AddWarning(notes, "无法读取 " + Path.GetFileName(manifest) + "：" + error.Message); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { complete = false; AddWarning(notes, "游戏库读取失败：" + library + "。" + error.Message); }
        }
        var appinfo = Path.Combine(currentRoot, "appcache", "appinfo.vdf");
        if (collected.Count > 0 && File.Exists(appinfo))
        {
            try
            {
                using var file = new FileStream(appinfo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                foreach (var (id, app) in SteamAppInfo.Read(file, collected.Keys.ToHashSet(), token))
                {
                    var game = collected[id]; collected[id] = game with { ProcessNames = SteamGameMetadata.ResolveProcesses(app, game.InstallPath) };
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            { complete = false; AddWarning(notes, "启动配置暂不可用，可为游戏手动选择程序文件。" + error.Message); }
        }
        else if (collected.Count > 0) { complete = false; AddWarning(notes, "Steam 未提供启动配置缓存，暂不能确认完整程序名单。"); }
        token.ThrowIfCancellationRequested();
        games = [.. collected.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)];
        warnings = [.. notes.Take(12).Select(x => x[..Math.Min(x.Length, 600)])]; root = currentRoot; scanId = Guid.NewGuid().ToString("N");
        scannedAt = DateTimeOffset.UtcNow; cachedGeneration = startGeneration;
    }
    public async Task<SteamToolScan> ReadAllAsync(bool refresh, CancellationToken token)
    {
        var collected = new List<SteamGame>(); var offset = 0; string? id = null; SteamCatalogPage first;
        first = await ReadAsync(refresh, 0, null, token); var page = first;
        while (true)
        {
            collected.AddRange(page.Games); if (page.NextOffset is not { } next) break;
            offset = next; id = first.ScanId; page = await ReadAsync(false, offset, id, token);
        }
        var entries = collected.SelectMany(g => g.ProcessNames.Select(n => new ProcessGroupEntry(n.ToLowerInvariant(), g.Name[..Math.Min(120, g.Name.Length)])))
            .DistinctBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
        var unknown = collected.Count(g => g.ProcessNames.Length == 0);
        var notes = first.Warnings.ToList();
        if (unknown > 0 && notes.Count < 12) notes.Add($"{unknown} 个安装项目没有可识别的 Windows 启动程序，不猜测文件名。");
        return new(entries, first.TotalCount, [.. notes], first.Complete);
    }
    private static void AddWarning(List<string> notes, string message) { if (notes.Count < 12) notes.Add(message); }
    private static string ReadText(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("Steam 文本元数据过大。");
        using var reader = new StreamReader(file, Encoding.UTF8); return reader.ReadToEnd();
    }
    private void Watch(string path, string[] filters, List<string> notes)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            foreach (var filter in filters) watcher.Filters.Add(filter);
            watcher.Changed += (_, _) => Interlocked.Increment(ref generation);
            watcher.Created += (_, _) => Interlocked.Increment(ref generation);
            watcher.Deleted += (_, _) => Interlocked.Increment(ref generation);
            watcher.Renamed += (_, _) => Interlocked.Increment(ref generation);
            watcher.Error += (_, _) => { watching = false; Interlocked.Increment(ref generation); };
            watcher.EnableRaisingEvents = true; watchers.Add(watcher);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { watcher?.Dispose(); watching = false; AddWarning(notes, "无法监听游戏库变化，下次打开工具会重新读取：" + path); }
    }
    public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); gate.Dispose(); }
}
