using Axiom.Domain;

namespace Axiom.Contracts;

public static class ApplicationAudioActivityCapabilities
{
    public const string Active = "audio.application-active";
    public static TaskCapability Descriptor { get; } = new(Active, 1, TaskCapabilityKind.Detection, "声音", "指定程序存在活动播放音频会话",
        "所选程序在Windows常规默认播放设备上存在活动渲染音频会话时条件成立，不需要媒体控制会话。",
        [new("executable", "检测的程序", TaskFieldKind.ExecutableSelection, true, "多选1–32完整exe路径；应选择实际播放进程，不只选择启动器。", null, ExecutablePaths.MaximumTextLength, [])],
        "任意所选程序有已核验活动会话即成立；已知全部无匹配或不活动才不成立。首次和未知恢复不补一次性动作；已知初始状态可保持唤醒，未知、暂停或结束释放。按需共享Windows通知，不轮询、不采集声音。UI退出后台仍检测。",
        "仅默认播放端点的AudioSessionStateActive，不证明实际听见、正在录音或会议进行；应用单独路由和跨进程会话不受保证。不读取麦克风、音量峰值、曲目或内容。身份无法核验为未知；默认设备变化重新建立基线，旧通知不沿用。原生查询5秒期限；最多32消费者/128会话/128路径资源/64提示/256状态样本，容量或通知故障为未知，需暂停重新启用。", IsStateCondition: true);
    public static string[] Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Active || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("executable", out var paths)) throw new ArgumentException("请选择指定程序的完整exe路径。");
        return ExecutablePaths.Parse(paths);
    }
}
