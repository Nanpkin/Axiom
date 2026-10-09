using Axiom.Domain;

namespace Axiom.Contracts;

public static class EndpointMuteCapabilities
{
    public const string State = "audio.mute-state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection,
        "音频", "默认设备静音状态", "检测默认通信麦克风或默认播放设备当前是否静音。",
        [new("endpoint", "默认设备", TaskFieldKind.Choice, true, "麦克风采用Windows通信角色；播放设备采用常规默认角色。", "input", 8,
            [new("input", "默认通信麦克风"), new("output", "默认播放设备")]),
         new("state", "条件", TaskFieldKind.Choice, true, "提醒等一次性动作仅响应可靠状态的后续变化；首次连接或切换默认设备不补发。", "unmuted", 8,
            [new("muted", "已静音"), new("unmuted", "已取消静音")])],
        "按需共享Windows端点静音通知。持续动作可使用首次已知状态；设备切换、设备不可用或通知失败时状态未知，持续动作释放。暂停、删除或断开后释放订阅。",
        "不录音、不读取音频内容；仅观察Windows端点软件静音，不证明硬件隐私开关、应用内静音或实际是否录音。应用自行选择的设备可能不同。设备标识仅在进程内短暂比较，不保存或传输。");

    public static (string Endpoint, bool Muted) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("endpoint", out var endpoint) || endpoint is not ("input" or "output") ||
            !invocation.Parameters.TryGetValue("state", out var state) || state is not ("muted" or "unmuted"))
            throw new ArgumentException("默认设备静音检测参数无效。");
        return (endpoint, state == "muted");
    }
}
