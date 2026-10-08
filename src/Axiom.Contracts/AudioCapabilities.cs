using Axiom.Domain;

namespace Axiom.Contracts;

public static class AudioCapabilities
{
    public const string Changed = "audio.default";
    public static TaskCapability Descriptor { get; } = new(Changed, 1, TaskCapabilityKind.Detection, "设备", "默认音频设备变化",
        "Windows通知默认播放设备或默认通信麦克风变化后执行。",
        [new("endpoint", "何时", TaskFieldKind.Choice, true, "只接收默认设备变化通知，不枚举设备或读取设备标识。", "output", 8,
            [new("output", "默认播放设备变化"), new("input", "默认通信麦克风变化")])],
        "按需共享Windows音频通知；首次不补发既有默认选择。暂停、修改或删除取消尚未执行的延迟。UI退出规则继续。",
        "不选择某个具体设备，也不证明人为插拔；没有默认设备也可能通知。播放采用常规默认角色，麦克风采用通信角色。应用单独选择的设备可能不随Windows默认变化；此检测不切换音频路由或修改音量。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Changed || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("endpoint", out var endpoint) || endpoint is not ("output" or "input")) throw new ArgumentException("音频检测配置无效。");
        return endpoint;
    }
}
