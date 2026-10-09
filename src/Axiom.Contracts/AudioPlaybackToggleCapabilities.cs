using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class AudioPlaybackToggleCapabilities
{
    public const string Toggle = "audio.playback-toggle";
    public static TaskCapability Descriptor { get; } = new(Toggle, 1, TaskCapabilityKind.Action, "声音", "切换默认播放设备静音",
        "读取执行开始时默认播放设备的静音状态，反向设置一次并读回。",
        [new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "已静音则取消静音，未静音则静音；无法读取时不写入。不改变音量或默认设备，不停止播放，不持续控制。UI退出后台仍可执行。",
        "取消静音可能立即发声，请核对音量。只处理执行开始时Windows常规默认播放设备，应用单独路由可能不受影响；执行中默认设备变化时不转而控制新设备。暂停不恢复，取消或超时可能已写入；不重试、不恢复旧值。请勿用静音状态变化触发此切换，以免反复切换。");
    public static int Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Toggle || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 86400)
            throw new ArgumentException("请填写0–86400的整数延迟；播放静音切换不接受其他操作参数。");
        return value;
    }
}
