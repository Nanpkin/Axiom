using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class AudioPlaybackAdjustCapabilities
{
    public const string Adjust = "audio.playback-adjust";
    public static TaskCapability Descriptor { get; } = new(Adjust, 1, TaskCapabilityKind.Action, "声音", "提高或降低默认播放音量",
        "读取执行开始时默认播放设备的音量，按指定百分点调整一次。",
        [new("operation", "操作", TaskFieldKind.Choice, true, "按当前音量相对调整，保持静音状态。", "decrease", 8,
            [new("increase", "提高音量"), new("decrease", "降低音量")]),
         new("step", "调整幅度（百分点）", TaskFieldKind.Number, true, "1–100整数，例如40%提高5个百分点变为45%；结果限制在0–100%。", "5", 3, [], Minimum: 1, Maximum: 100),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "由规则调度；暂停取消尚未执行的动作。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "一次读取、写入和读回，不取消静音、不改默认设备、不自动恢复。关闭Axiom窗口后后台仍按规则执行；与其他内置音频动作共享原生准入，不排队重试。",
        "影响默认播放设备上的所有应用，单独路由可能不受影响。默认设备执行中变化时不转而操作新设备。外部音量修改可能与本次读取交错；读回不一致会失败，不持续压制。取消或超时可能已经修改，不重试、不恢复旧值。");
    public static (string Operation, int Step, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Adjust || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 3 ||
            !invocation.Parameters.TryGetValue("operation", out var operation) || operation is not ("increase" or "decrease") ||
            !invocation.Parameters.TryGetValue("step", out var step) || !int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out var s) || s is < 1 or > 100 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择提高或降低音量，并填写1–100整数调整幅度和0–86400整数延迟。");
        return (operation, s, d);
    }
}
