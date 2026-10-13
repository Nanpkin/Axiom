using Axiom.Domain;

namespace Axiom.Contracts;

public static class TaskbarAutoHideRestoreCapabilities
{
    public const string Restore = "taskbar.auto-hide.restore";
    public static TaskCapability Descriptor { get; } = TaskbarAutoHideCapabilities.Descriptor with
    {
        Id = Restore, Name = "条件期间设置任务栏自动隐藏，结束恢复",
        Description = "条件成立期间开启或关闭自动隐藏，结束时可恢复本轮最初选项。",
        Fields = [.. TaskbarAutoHideCapabilities.Descriptor.Fields.Select(f => f.Id == "delaySeconds" ? f with { Hint = "条件须持续满足所设延迟；首次已知成立也可应用。" } : f)],
        Behavior = "读取真实已知原设置才写一次并读回，获胜规则更新保留本轮最初基线。条件结束、未知、暂停、删除、连接结束或正常停止时，只在同一本次系统Shell的自动隐藏选项仍等于最后确认值时恢复；其他标志使用恢复时的当前值。外部改变或身份未知让出，不周期重写；UI退出规则继续。",
        EditWarning = "显示本轮确认的控制租约，不代表全局设置。固定本次Explorer和任务栏，Shell重启或设置失读后不操作替代任务栏；未确认写入不恢复、不重试。只改变自动隐藏选项，鼠标边缘或系统仍可展开；不是完全隐藏或持续强制。外部修改只在本次更新或释放时核验，不证明来源是人为操作。"
    };
    public static void Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Restore) throw new ArgumentException("条件恢复任务栏设置无效。");
        RestorableSettingInvocations.ValidateLease(invocation);
        TaskbarAutoHideCapabilities.Validate(invocation with { CapabilityId = TaskbarAutoHideCapabilities.Set });
    }
    public static string ResourceId(TaskInvocation invocation) { Validate(invocation); return "system-taskbar"; }
}
