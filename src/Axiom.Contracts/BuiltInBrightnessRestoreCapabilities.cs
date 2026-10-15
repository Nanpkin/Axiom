using Axiom.Domain;

namespace Axiom.Contracts;

public static class BuiltInBrightnessRestoreCapabilities
{
    public const string Set = "display.brightness.restore";
    public static TaskCapability Descriptor { get; } = BuiltInBrightnessCapabilities.Descriptor with
    {
        Id = Set, Name = "条件期间设置屏幕亮度并恢复（自动选择）",
        Description = "首次优先捕获内屏；明确没有内置亮度接口时捕获主屏硬件亮度。条件结束、未知、暂停或删除后尝试恢复本轮最初值。",
        Behavior = "只在首次捕获时自动选择后端与屏幕，之后不重新选择；参数更新保留原目标和最初基线。内屏查询未知或设置失败不切换外屏。内屏与硬件屏共用一个亮度控制租约，条件控制期间其他Axiom亮度动作和恢复请求都会拒绝。请先暂停已有亮度恢复任务并等待释放完成，再调整亮度或启用另一恢复任务；不排队，UI退出后后台继续。",
        EditWarning = "只有目标身份/范围仍已知且当前仍等于Axiom最后确认值才恢复。其他来源改变、拓扑变化、设备未知或未确认写入会让出，不覆盖或改控新屏幕。范围选择在本租约内不能改变；部分或恢复失败会说明，不重试。异常进程退出不能保证恢复；原生阻塞时继续持有句柄与准入直到真正返回。"
    };
    public static void Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Set) throw new ArgumentException("亮度恢复能力不匹配。");
        BuiltInBrightnessCapabilities.Validate(invocation with { CapabilityId = BuiltInBrightnessCapabilities.Set });
    }
}
