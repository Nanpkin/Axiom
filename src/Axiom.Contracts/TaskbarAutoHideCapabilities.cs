using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class TaskbarAutoHideCapabilities
{
    public const string Set = "taskbar.auto-hide";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "桌面", "设置任务栏自动隐藏",
        "一次开启或关闭Windows设置中的任务栏自动隐藏。",
        [new("state", "任务栏行为", TaskFieldKind.Choice, true, "关闭自动隐藏后始终显示；不完全隐藏任务栏。", "enable", 8,
            [new("enable", "开启自动隐藏"), new("disable", "关闭自动隐藏（始终显示）")]),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "先读取当前设置，只修改自动隐藏选项，发出一次设置请求后读回；已一致则无需修改。之后保留该设置，暂停、删除或条件结束不恢复；可在Windows设置中手动更改，UI退出后台规则继续。",
        "只操作当前用户活动会话的Windows系统任务栏，不逐屏选择。自动隐藏仍可因鼠标移到边缘、通知或系统行为展开，不保证画面始终无任务栏。系统Shell缺失、重启、身份或读数无法核验时不写；5秒单准入，取消或超时可能已修改，不重试、不持续强制或恢复。");
    public static (bool Enabled, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("state", out var state) || state is not ("enable" or "disable") ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择开启或关闭自动隐藏，并填写0–86400的整数延迟。");
        return (state == "enable", d);
    }
}
