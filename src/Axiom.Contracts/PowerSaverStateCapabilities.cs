using Axiom.Domain;

namespace Axiom.Contracts;

public static class PowerSaverStateCapabilities
{
    public const string State = "power.saver-state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "电源", "Windows报告的电池节能状态",
        "Windows电池节能状态API报告开启或关闭时条件成立，不按电量阈值猜测。",
        [new("state", "报告的节能状态", TaskFieldKind.Choice, true, "仅接受Windows明确报告的开启或关闭；未知不当关闭。", "on", 3,
            [new("on", "已开启"), new("off", "已关闭")])],
        "共享Windows节能状态通知后读取当前报告，无周期查询。首次、重新启用、睡眠恢复及未知恢复只建立基线，不补一次性动作。退出界面后后台继续检测；不启用或关闭系统节能。",
        "使用公开电池节能兼容API，只报告Windows提供的0/1状态，不代表新版节能强度，也不证明电脑有电池或支持相关界面。查询、通知异常或其他报告值为未知；不以低电量、电源方案或电源模式代替此状态。短暂变化可能在查询前结束；动作的新值不自动恢复。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("state", out var state) || !ValidResourceId(state))
            throw new ArgumentException("请选择Windows报告的节能状态开启或关闭。");
        return state;
    }
    public static bool ValidResourceId(string id) => id is "on" or "off";
}
