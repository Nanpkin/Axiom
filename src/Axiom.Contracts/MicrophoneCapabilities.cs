using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class MicrophoneCapabilities
{
    public const string Set = "audio.microphone";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "声音", "静音或取消静音默认通信麦克风",
        "对执行开始时Windows默认通信麦克风设置一次静音状态。",
        [new("operation", "操作", TaskFieldKind.Choice, true, "明确设为静音或取消静音，不使用状态不明的切换操作。", "mute", 8,
            [new("mute", "静音"), new("unmute", "取消静音")]),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "由规则调度；暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "触发时设置一次并立即读回；不修改输入音量，不持续控制。之后可手动修改，暂停/结束不恢复旧状态。取消静音可能立即恢复收音。",
        "只影响执行开始时的默认通信输入设备；应用自行选择的其他麦克风不受保证。不是硬件隐私开关，不停止录音程序，不保证所有应用停止收音。无设备/权限/读回失败会说明；超时可能已修改但结果未确认，不自动重试或恢复。");
    public static (string Operation, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("operation", out var operation) || operation is not ("mute" or "unmute") ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择麦克风静音操作，并填写有效的整数延迟。");
        return (operation, d);
    }
}
