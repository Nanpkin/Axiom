using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class MicrophoneToggleCapabilities
{
    public const string Toggle = "audio.microphone-toggle";
    public static TaskCapability Descriptor { get; } = new(Toggle, 1, TaskCapabilityKind.Action, "声音", "切换默认通信麦克风静音",
        "读取执行开始时的默认通信麦克风静音状态，反向设置一次并读回。",
        [new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "已静音则取消静音，未静音则静音；状态无法读取时不修改。只改变软件静音，不改输入音量、不持续控制，暂停或条件结束不恢复。",
        "取消静音可能立即恢复收音；不是硬件隐私开关，不停止录音程序。只处理执行开始时Windows默认通信输入设备，应用自选输入不受保证。无设备、权限或读回失败明确说明；取消或超时可能已修改，不自动重试或恢复。");

    public static int Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Toggle || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 86400)
            throw new ArgumentException("请填写有效的整数延迟；麦克风切换不接受其他操作参数。");
        return value;
    }
}
