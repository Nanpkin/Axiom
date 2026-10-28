using Axiom.Domain;
namespace Axiom.Contracts;
public sealed record GameModeSettings(long Version = 0, bool ManualEnabled = false, bool AutomaticEnabled = false, string[]? ProcessNames = null);
public sealed record GameModeSnapshot(GameModeSettings Settings, bool? Active, string Reason);
public static class GameModeCapabilities
{
    public const string State = "axiom.game-mode";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "Axiom", "游戏模式状态", "按游戏模式开启或关闭组合条件。",
        [new("state", "游戏模式", TaskFieldKind.Choice, true, "手动开启或指定程序运行时开启。", "on", 3, [new("on", "开启"), new("off", "关闭")])],
        "只响应状态变化；逻辑与不会停止其他条件的采集。", "状态未知时不执行动作。", IsStateCondition: true);
    public static GameModeSettings Normalize(GameModeSettings settings)
    {
        var names = settings.ProcessNames ?? [];
        if (settings.Version < 0 || names.Length > 128 || names.Any(n => n is null || !ProcessGroups.ValidProcessName(n))) throw new ArgumentException("请选择最多128个有效的exe程序名。");
        names = [.. names.Select(n => n.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (settings.AutomaticEnabled && names.Length == 0) throw new ArgumentException("自动游戏模式需要至少一个程序。");
        return settings with { ProcessNames = names };
    }
    public static bool? Active(GameModeSettings settings, IEnumerable<bool?> processes)
    {
        if (settings.ManualEnabled) return true;
        if (!settings.AutomaticEnabled) return false;
        var unknown = false;
        foreach (var process in processes) { if (process == true) return true; unknown |= process is null; }
        return unknown ? null : false;
    }
}
