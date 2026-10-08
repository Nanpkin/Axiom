namespace Axiom.Contracts;

public sealed record ProcessGroupEntry(string ProcessName, string DisplayName);
public sealed record ProcessGroup(string Id, long Version, string Name, ProcessGroupEntry[] Entries, bool AutoManaged = false);
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
        return group with { Name = group.Name.Trim(), Entries = [.. group.Entries
            .DistinctBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x with { ProcessName = x.ProcessName.ToLowerInvariant(), DisplayName = x.DisplayName.Trim() })] };
    }
}
