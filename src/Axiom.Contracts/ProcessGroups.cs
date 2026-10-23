namespace Axiom.Contracts;

public sealed record ProcessGroupEntry(string ProcessName, string DisplayName);
public sealed record ProcessGroup(string Id, long Version, string Name, ProcessGroupEntry[] Entries, bool AutoManaged = false,
    string[]? IncludedGroupIds = null, ProcessGroupEntry[]? ResolvedEntries = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ProcessGroupEntry[] EffectiveEntries => ResolvedEntries ?? Entries;
}
public sealed record SteamGame(uint AppId, string Name, string InstallPath, long SizeOnDisk, string[] ProcessNames);
public sealed record SteamCatalogPage(string ScanId, DateTimeOffset ScannedAtUtc, bool FromCache,
    SteamGame[] Games, int TotalCount, int? NextOffset, string[] Warnings, bool Complete = true);

public static class ProcessGroups
{
    public const int MaximumGroups = 32;
    public const int MaximumEntries = 128;
    public const int MaximumTotalEntries = 512;
    public static bool ValidProcessName(string name) => name.Length is >= 5 and <= 132 &&
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\r', '\n', '\0']) < 0 &&
        name == name.Trim() && !name.Any(char.IsControl);
    public static string[] SplitNames(string? text) => (text ?? "").Split(['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static ProcessGroup Normalize(ProcessGroup group)
    {
        if (!Guid.TryParseExact(group.Id, "D", out _) || group.Version < 0 || string.IsNullOrWhiteSpace(group.Name) ||
            group.Name.Trim().Length > 80 || group.Entries is null || group.Entries.Length > MaximumEntries ||
            group.Entries.Any(x => x is null || !ValidProcessName(x.ProcessName) || string.IsNullOrWhiteSpace(x.DisplayName) || x.DisplayName.Length > 120))
            throw new ArgumentException("请填写组名称和有效的 .exe 文件名，每组最多 128 个程序。");
        var references = group.IncludedGroupIds ?? [];
        if (references.Length > MaximumGroups || references.Any(id => !Guid.TryParseExact(id, "D", out _)))
            throw new ArgumentException("请选择有效的已有进程组。");
        var id = Guid.Parse(group.Id).ToString("D");
        references = references.Select(value => Guid.Parse(value).ToString("D")).Distinct(StringComparer.Ordinal).ToArray();
        if (references.Contains(id)) throw new ArgumentException("进程组不能包含自身。");
        return group with { Id = id, Name = group.Name.Trim(), IncludedGroupIds = references, ResolvedEntries = null, Entries = [.. group.Entries
            .DistinctBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x with { ProcessName = x.ProcessName.ToLowerInvariant(), DisplayName = x.DisplayName.Trim() })] };
    }

    public static ProcessGroup[] Resolve(ProcessGroup[] groups)
    {
        if (groups.Length > MaximumGroups) throw new InvalidOperationException("最多保存 32 个进程组。");
        var normalized = groups.Select(Normalize).ToArray();
        if (normalized.Select(g => g.Id).Distinct().Count() != normalized.Length)
            throw new ArgumentException("进程组标识重复。");
        var byId = normalized.ToDictionary(g => g.Id);
        var resolved = new Dictionary<string, ProcessGroup>();
        var visiting = new HashSet<string>();
        ProcessGroup Expand(string id)
        {
            if (resolved.TryGetValue(id, out var cached)) return cached;
            if (!byId.TryGetValue(id, out var group)) throw new ArgumentException("包含的进程组已不存在，请刷新后重新选择。");
            if (!visiting.Add(id)) throw new ArgumentException("进程组不能相互循环包含。");
            // Local members take precedence over inherited display names; the first selected group wins ties.
            var entries = group.Entries.Concat(group.IncludedGroupIds!.SelectMany(child => Expand(child).EffectiveEntries))
                .DistinctBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase).ToArray();
            if (entries.Length > MaximumEntries) throw new InvalidOperationException($"“{group.Name}”包含已有组后超过 128 个程序，请减少成员。");
            visiting.Remove(id);
            return resolved[id] = group with { ResolvedEntries = entries };
        }
        var result = normalized.Select(g => Expand(g.Id)).ToArray();
        if (result.Sum(g => g.EffectiveEntries.Length) > MaximumTotalEntries)
            throw new InvalidOperationException("所有进程组展开后合计最多 512 个程序，请先清理不需要的成员。");
        return result;
    }
}
