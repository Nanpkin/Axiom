namespace Axiom.Infrastructure;

public static class GameHdrTaskPresentation
{
    public static string ResultTitle(string? state) => state switch
    {
        null => "尚无结果",
        "等待会话程序" or "会话程序已断开" => "检测暂不可用",
        "监控中" => "等待程序变化",
        _ => state
    };

    public static string ResultReason(string? state, string? reason) => state switch
    {
        null => "连接后台后，可以查看最近发生的事情。",
        "等待会话程序" or "会话程序已断开" => "暂时无法检测程序。请重新打开 Axiom，再刷新检查。已有游戏的 HDR 不会被强行恢复。",
        "检测异常" or "检测中断" => "无法确认程序状态，已停止自动切换。请重新打开 Axiom 后检查。",
        "切换失败" => "未能确认 HDR 已达到目标。请检查显示器连接和 Windows 的 HDR 设置，再重新启动游戏。",
        "检测到游戏" => "游戏已启动，正在检查需要的 HDR 设置。",
        _ => reason ?? "暂时没有更多信息。"
    };

    public static string DisplayState(bool? enabled, bool? active, bool reachable)
    {
        if (!reachable) return "当前状态未确认，请刷新后查看。";
        return (enabled, active) switch
        {
            (true, true) => "HDR 已开启并生效",
            (false, false) => "HDR 已关闭",
            (true, false) => "HDR 已开启，但尚未生效",
            _ => "HDR 状态未确认"
        };
    }
}
