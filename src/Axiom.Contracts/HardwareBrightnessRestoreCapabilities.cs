using Axiom.Domain;

namespace Axiom.Contracts;

public static class HardwareBrightnessRestoreCapabilities
{
    public const string Set = "display.hardware-brightness.restore";
    public static TaskCapability Descriptor { get; } = HardwareBrightnessCapabilities.Descriptor with
    {
        Id = Set, Name = "条件期间设置支持硬件控制的屏幕亮度并恢复",
        Description = "条件成立时捕获已知亮度基线，设置一次；条件结束、未知、暂停或删除后尝试恢复本轮最初值。",
        Behavior = "只控制本次捕获的屏幕；后续参数更新保留最初基线，不重复相对累加。内屏与硬件屏共用一个亮度控制租约，条件控制期间其他Axiom亮度动作和恢复请求都会拒绝。请先暂停已有亮度恢复任务并等待释放完成，再调整亮度或启用另一恢复任务；不排队，UI退出后后台继续。",
        EditWarning = "只有目标身份/范围仍已知且当前仍等于Axiom最后确认值才恢复。其他来源改变、拓扑变化、设备未知或未确认写入会让出，不覆盖或改控新屏幕。范围选择在本租约内不能改变；部分或恢复失败会说明，不重试。异常进程退出不能保证恢复；原生阻塞时继续持有句柄与准入直到真正返回。"
    };
    public static void Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set) throw new ArgumentException("亮度恢复能力不匹配。");
        HardwareBrightnessCapabilities.Validate(invocation with { CapabilityId = HardwareBrightnessCapabilities.Set });
    }
}
