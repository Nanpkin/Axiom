using Axiom.Domain;

namespace Axiom.Contracts;

public static class MediaPlaybackCapabilities
{
    public const string State = "media.playback-state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "媒体", "Windows当前媒体播放状态",
        "检测Windows选中的当前媒体会话正在播放、已暂停或已停止。",
        [new("state", "播放状态", TaskFieldKind.Choice, true, "没有当前媒体会话不算暂停或停止；不是所有应用的音频状态。", "playing", 7,
            [new("playing", "正在播放"), new("paused", "已暂停"), new("stopped", "已停止或已关闭")])],
        "按需共享系统媒体通知，无轮询。首次、当前会话切换及未知后的首个有效状态只建基线，不补一次性动作；持续动作可使用首次已知状态。暂停或删除后释放订阅。",
        "播放器须支持Windows媒体控制。仅报告Windows选中的会话，不指定程序、不读取曲目，也不证明实际出声。没有会话时三个条件均不成立；访问、查询或通知失败为未知，保持唤醒会释放。不组合会改变媒体状态的动作。", IsStateCondition: true);
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("state", out var state) || !ValidResource(state))
            throw new ArgumentException("请选择当前媒体正在播放、已暂停或已停止。");
        return state;
    }
    public static bool ValidResource(string resource) => resource is "playing" or "paused" or "stopped";
}
