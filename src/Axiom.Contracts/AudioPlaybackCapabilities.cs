using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class AudioPlaybackCapabilities
{
    public const string Set = "audio.playback";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "声音", "调整默认播放音量或静音",
        "对执行开始时Windows默认播放设备设置一次主音量或静音。",
        [new("operation", "操作", TaskFieldKind.Choice, true, "只改所选设置，不持续控制，不自动恢复。", "mute", 8,
            [new("volume", "设置音量"), new("mute", "静音"), new("unmute", "取消静音")]),
         new("percent", "音量（%）", TaskFieldKind.Number, true, "0–100整数；只在设置音量时使用，设置音量不会取消静音。", "30", 3, [], Minimum: 0, Maximum: 100),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "由规则调度，暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "触发时调整一次并立即读回；之后可手动修改。暂停不恢复旧音量，不关闭或停止正在播放的应用。相同默认播放资源由规则仲裁，原生音频调用串行且不排队重试。",
        "影响该设备上的所有应用，不是单应用音量或麦克风。应用单独路由可能不受影响；默认设备在执行中变化时不会转而操作新设备。取消静音可能立即发声，请先核对音量。无设备/权限/读回失败会解释；调用超时可能已修改，结果未确认且不自动重试。");
    public static (string Operation, int Percent, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 3 ||
            !invocation.Parameters.TryGetValue("operation", out var operation) || operation is not ("volume" or "mute" or "unmute") ||
            !invocation.Parameters.TryGetValue("percent", out var percent) || !int.TryParse(percent, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 0 or > 100 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择音量操作，并填写有效的整数音量和延迟。");
        return (operation, p, d);
    }
}
