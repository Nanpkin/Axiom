using Axiom.Domain;

namespace Axiom.Contracts;

public static class SunshineCapabilities
{
    public const string State = "sunshine.stream-state";
    public const string Connected = "connected";
    public const string Resumable = "resumable";
    public const string Either = "either";

    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "会话", "Sunshine 串流状态",
        "检测本机 Foundation Sunshine 是否正在串流，或断开后仍可继续。",
        [new("state", "串流状态", TaskFieldKind.Choice, true, "三个选项共用一条本机推送连接。", Either, 16,
            [new(Connected, "串流已连接"), new(Resumable, "已断开、可继续"), new(Either, "任一串流状态")])],
        "订阅 Foundation Sunshine 的托盘状态推送，首次收到状态时建立基线，无定时状态查询。",
        "已连接以 Sunshine 报告的音视频会话启动为准。推送中断、配对提示或托盘通知遮盖状态时显示未知。需要配置本机 Sunshine 推送访问。", IsStateCondition: true);

    public static string Label(string state) => state switch
    {
        Connected => "Sunshine 串流已连接",
        Resumable => "Sunshine 已断开、可继续",
        _ => "Sunshine 任一串流状态"
    };

    public static bool IsState(string value) => value is Connected or Resumable or Either;

    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("state", out var state) || !IsState(state))
            throw new ArgumentException("请选择 Sunshine 串流状态。");
        return state;
    }
}
