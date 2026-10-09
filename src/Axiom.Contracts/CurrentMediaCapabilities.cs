using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class CurrentMediaCapabilities
{
    public const string Control = "media.current-control";
    public static TaskCapability Descriptor { get; } = new(Control, 1, TaskCapabilityKind.Action, "媒体", "控制Windows当前媒体会话",
        "向Windows选中的当前媒体会话请求暂停、播放、上一项或下一项一次。",
        [new("operation", "操作", TaskFieldKind.Choice, true, "目标由Windows在执行时选择，可能是播放器或浏览器；不是指定程序。", "pause", 8,
            [new("pause", "暂停"), new("play", "播放"), new("previous", "上一项"), new("next", "下一项")]),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "延迟结束时再确定Windows当前媒体会话；暂停任务取消尚未发出的请求。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "只处理执行时Windows选中的一个媒体会话，不启动播放器、不模拟按键、不读取曲目或媒体内容。已暂停时暂停、已播放时播放会说明无需操作；成功只代表媒体会话接受请求，不证明播放状态或曲目已经改变。关闭Axiom窗口后后台仍会执行。",
        "播放器须接入Windows媒体控制并支持所选操作；没有会话、操作不支持或访问被拒绝时失败。执行前发现会话切换会跳过；检查后瞬间切换仍可能控制刚才固定的会话，不会改控新会话。播放可能立即发声，上一项/下一项的含义由播放器决定。暂停任务不恢复，取消或超时可能已经发送请求，不自动重试。");

    public static (string Operation, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Control || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("operation", out var operation) || operation is not ("pause" or "play" or "previous" or "next") ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择Windows当前媒体会话的暂停、播放、上一项或下一项，并填写0–86400的整数延迟；不支持指定程序或切换播放状态。");
        return (operation, d);
    }
}
