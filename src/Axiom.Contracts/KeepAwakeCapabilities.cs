using Axiom.Domain;

namespace Axiom.Contracts;

public static class KeepAwakeCapabilities
{
    public const string Hold = "power.keepAwake";
    public static TaskCapability Descriptor { get; } = new(Hold, 1, TaskCapabilityKind.Action,
        "电源", "条件期间保持唤醒", "条件成立期间持有 Axiom 自己的 Windows 电源请求，结束后释放。",
        [new("target", "保持唤醒", TaskFieldKind.Choice, true, "保持屏幕时也保持系统运行。", "system", 16,
            [new("system", "仅系统（屏幕可关闭）"), new("display", "系统及屏幕")])],
        "多个规则共享同类请求；全部条件结束、状态未知、暂停、删除或连接中断后释放。不会更改电源计划。",
        "仅请求阻止自动空闲休眠或息屏；手动睡眠、锁屏、电源策略或系统覆盖仍可能优先生效。无法保证电脑始终保持运行。");

    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Hold || invocation.SchemaVersion != 1 || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("target", out var target) || target is not ("system" or "display"))
            throw new ArgumentException("保持唤醒参数无效。");
        return target;
    }
}
