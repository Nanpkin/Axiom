using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class ApplicationAudioCapabilities
{
    public const string Set = "audio.application";
    public static TaskCapability Descriptor { get; } = new(Set, 1, TaskCapabilityKind.Action, "声音", "调整指定应用播放音量或静音",
        "只调整当前默认播放设备上所选应用的活动音频会话一次。",
        [new("executable", "目标应用", TaskFieldKind.ExecutableSelection, true, "多选1–32个完整exe路径；实际发声进程可能与启动器不同。", null, ExecutablePaths.MaximumTextLength, []),
         new("operation", "操作", TaskFieldKind.Choice, true, "只改所选设置，不持续控制、不自动恢复。", "volume", 8,
            [new("volume", "设置音量"), new("mute", "静音"), new("unmute", "取消静音")]),
         new("percent", "音量（%）", TaskFieldKind.Number, true, "0–100整数；仅设置音量时使用，不会取消静音。", "30", 3, [], Minimum: 0, Maximum: 100),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "执行时核对完整路径、当前用户和会话，调整所有匹配的活动音频会话并读回。暂停不恢复，后续手动修改不受干预。部分成功会说明，取消或超时可能已经改变部分会话，不重试。",
        "应用未播放或没有匹配活动会话时失败，不等待应用以后播放；应用路由到其他设备、跨进程共享或身份无法核对的会话不会修改。不会改变设备主音量、其他应用或麦克风。取消静音可能立即发声；设备在执行中变化时不转到新设备。");

    public static (string[] Paths, string Operation, int Percent, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 4 ||
            !invocation.Parameters.TryGetValue("executable", out var executable) ||
            !invocation.Parameters.TryGetValue("operation", out var operation) || operation is not ("volume" or "mute" or "unmute") ||
            !invocation.Parameters.TryGetValue("percent", out var percent) || !int.TryParse(percent, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 0 or > 100 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择完整程序路径、音量操作，并填写有效整数音量和延迟。");
        return (ExecutablePaths.Parse(executable), operation, p, d);
    }
}
