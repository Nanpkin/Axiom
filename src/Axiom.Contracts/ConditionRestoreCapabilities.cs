using Axiom.Domain;

namespace Axiom.Contracts;

public static class ConditionRestoreCapabilities
{
    public const string Playback = "audio.playback.restore";
    public const string Microphone = "audio.microphone.restore";
    public const string PowerScheme = "power.scheme.restore";
    public const string ProgramPriority = "program.priority.restore";
    public const string WindowState = "window.state.restore";
    public static TaskCapability PlaybackDescriptor { get; } = Describe(AudioPlaybackCapabilities.Descriptor, Playback, "条件期间调整播放音量或静音，结束恢复");
    public static TaskCapability MicrophoneDescriptor { get; } = Describe(MicrophoneCapabilities.Descriptor, Microphone, "条件期间设置麦克风静音，结束恢复");
    public static TaskCapability PowerSchemeDescriptor { get; } = Describe(PowerSchemeCapabilities.Descriptor, PowerScheme, "条件期间切换电源方案，结束恢复") with
    { Description = "条件期间使用已有标准方案，结束时可恢复本轮原标准或自定义方案。" };
    public static TaskCapability ProgramPriorityDescriptor { get; } = Describe(ProgramPriorityCapabilities.Descriptor, ProgramPriority, "条件期间设置程序优先级，结束恢复") with
    {
        Fields = [.. ProgramPriorityCapabilities.Descriptor.Fields.Select(f => f.Id == "executable" ? f with
        { Kind = TaskFieldKind.FilePath, MaximumLength = 1024, Hint = "首版选择一个完整exe路径；固定本轮已运行的全部同用户同会话实例，不控制新实例。" } : f)],
        EditWarning = "本恢复动作首版单完整exe路径，避免重叠多名单绕过资源仲裁；原多选一次性动作不变。保留本轮进程句柄/创建身份，已退出、身份未知或优先级外部改变不写旧值到后来实例；多实例可能部分完成，逐类说明。不是CPU使用率限制。"
    };
    public static TaskCapability WindowStateDescriptor { get; } = Describe(WindowCapabilities.StateDescriptor, WindowState, "条件期间整理窗口，结束恢复原状态和位置") with
    {
        Description = "固定一个完整exe路径的本轮当前可见主窗口，按状态或布局整理；结束有条件恢复原WindowPlacement。",
        Fields = [ProgramPriorityDescriptor.Fields.Single(f => f.Id == "executable") with { Hint = "首版一个完整exe路径、捕获当前一个可见主窗口；不等待或控制后来窗口，不读取标题。" },
            WindowCapabilities.StateDescriptor.Fields.Single(f => f.Id == "state") with { Choices = [.. WindowCapabilities.StateDescriptor.Fields.Single(f => f.Id == "state").Choices.Where(c => c.Value is not ("topmost" or "notopmost"))], Hint = "状态及当前屏布局；首版不恢复置顶关系或移到另一屏。" },
            PlaybackDescriptor.Fields.Single(f => f.Id == "delaySeconds")],
        EditWarning = "捕获原WindowPlacement（显示状态、正常位置和大小），完整原窗口身份/显示拓扑及当前最后确认placement相等才恢复。拖动、调整、销毁、替代窗口、Unknown或拓扑变化让出，不能证明人为。窗口限制或超时可能部分调整，不重试。原多选/标题/等待/置顶一次性动作不变。"
    };
    private static TaskCapability Describe(TaskCapability original, string id, string name) => original with
    {
        Id = id, Name = name, Description = "条件成立期间请求所选设置，结束时有条件恢复本轮最初设置。",
        Fields = [.. original.Fields.Select(f => f.Id == "delaySeconds" ? f with { Hint = "条件须持续满足所设延迟；首次已知成立也可应用。" } : f.Id == "operation" ? f with { Hint = "只控制所选字段；明确设置，不使用切换操作。" } : f)],
        Behavior = "真实读取已知基线才写入并读回；同资源规则经仲裁，同一租约的获胜值更新保留最初基线。条件结束、Unknown、暂停、删除、断开连接或正常停止后，仅当前仍等于Axiom最后确认值才恢复。外部改变或身份未知让出，不证明是人为操作，不周期重写。",
        EditWarning = "观察显示Axiom确认的本轮控制租约，不代表全系统设置。固定本次实际目标，不改新默认端点或替代设备；超时/未确认写入不假称成功或恢复，不重试。默认播放只改所选音量或静音，麦克风只改通信输入静音；取消静音可能发声或收音。电源方案原自定义GUID只在内存中恢复，不创建缺失方案。UI退出继续，暂停收回控制。"
    };
    public static void Validate(TaskInvocation invocation)
    {
        RestorableSettingInvocations.ValidateLease(invocation);
        if (invocation.CapabilityId == Playback) AudioPlaybackCapabilities.Validate(invocation with { CapabilityId = AudioPlaybackCapabilities.Set });
        else if (invocation.CapabilityId == Microphone) MicrophoneCapabilities.Validate(invocation with { CapabilityId = MicrophoneCapabilities.Set });
        else if (invocation.CapabilityId == PowerScheme) PowerSchemeCapabilities.Validate(invocation with { CapabilityId = PowerSchemeCapabilities.Set });
        else if (invocation.CapabilityId == ProgramPriority)
        {
            var paths = ProgramPriorityCapabilities.Validate(invocation with { CapabilityId = ProgramPriorityCapabilities.Set }).Paths;
            if (paths.Length != 1 || invocation.Parameters["executable"].Any(char.IsControl)) throw new ArgumentException("程序优先级恢复首版只能选择一个完整exe路径。");
        }
        else if (invocation.CapabilityId == WindowState)
        {
            if (invocation.Parameters.Count != 3 || !invocation.Parameters.TryGetValue("executable", out var path) || path.Any(char.IsControl) ||
                !invocation.Parameters.TryGetValue("state", out var state) || !WindowStateDescriptor.Fields.Single(f => f.Id == "state").Choices.Any(c => c.Value == state))
                throw new ArgumentException("请填写一个完整程序路径、窗口状态/布局和有效延迟。");
            ExecutablePaths.ValidatePath(path);
        }
        else throw new ArgumentException("未注册此条件恢复设置。");
    }
    public static string ResourceId(TaskInvocation invocation) => invocation.CapabilityId switch
    {
        Playback => invocation.Parameters["operation"] == "volume" ? "default-playback-volume" : "default-playback-mute",
        Microphone => "default-communication-mute",
        PowerScheme => "active-power-scheme",
        ProgramPriority or WindowState => ExecutablePaths.ValidatePath(invocation.Parameters["executable"]).ToLowerInvariant(),
        _ => throw new ArgumentException("条件恢复资源无效。")
    };
}
