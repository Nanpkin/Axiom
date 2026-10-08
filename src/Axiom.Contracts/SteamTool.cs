using System.Text.Json;

namespace Axiom.Contracts;

public sealed record SteamToolSettings(long Version, bool Enabled, string[] ExcludedProcesses);
public sealed record SteamToolSnapshot(SteamToolSettings Settings, string? GroupId, DateTimeOffset? LastScanUtc,
    DateTimeOffset? LastSuccessUtc, bool Scanning, int InstalledGames, int DetectedCount, int ExportedCount,
    string? Error, string[] Warnings, string ScanId, ProcessGroupEntry[] Processes, int? NextOffset);

public static class SteamTool
{
    public const string GroupName = "本机上的Steam游戏";
    public const int MaximumExclusions = 128;
    public const int PageSize = 64;
    public static SteamToolSettings Normalize(SteamToolSettings settings)
    {
        if (settings.Version < 0 || settings.ExcludedProcesses is null || settings.ExcludedProcesses.Length > MaximumExclusions ||
            settings.ExcludedProcesses.Any(n => n is null || !ProcessGroups.ValidProcessName(n)))
            throw new ArgumentException("排除项请填写 .exe 文件名，最多 128 个。");
        var result = settings with { ExcludedProcesses = [.. settings.ExcludedProcesses.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => n.ToLowerInvariant())] };
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > AutomationProtocol.MaximumFrame / 2)
            throw new ArgumentException("排除名单过长，请减少内容。");
        return result;
    }
}
