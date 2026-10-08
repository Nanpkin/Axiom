using System.Collections.Immutable;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class AxiomStartupPlugin : IAutomationPlugin
{
    public const string CapabilityId = "axiom.started";
    public string Id => "axiom.lifecycle";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new AxiomStartupTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [new(CapabilityId, "program.start"), new(CapabilityId, "program.close")];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(CapabilityId, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.Service, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation,
        EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "same user", []),
        ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class AxiomStartupTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor { get; } = new(AxiomStartupPlugin.CapabilityId, 1, TaskCapabilityKind.Detection,
        "Axiom", "Axiom 启动时", "Axiom 后台启动并准备好执行动作时触发。",
        [new("frequency", "启动次数", TaskFieldKind.Choice, true,
            "首次启动仅在本次 Windows 开机第一次启动 Axiom 后台时触发，重复启动不会执行；错过首次不会补执行。",
            "every", 16, [new("every", "每次启动时"), new("firstBoot", "本次开机首次启动时")])],
        "关闭再打开窗口不会触发。保存、编辑或启用后，从下一次后台启动生效。",
        "暂停、修改或删除会取消尚未执行的延迟动作；失败或取消不自动补执行。");
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) =>
        throw new ArgumentException("启动检测需要任务标识。");
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope, string taskId)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        if (!Guid.TryParseExact(taskId, "D", out _)) throw new ArgumentException("启动检测任务标识无效。");
        return RuleCondition.Equal(new(AxiomStartupPlugin.CapabilityId, taskId, scope), StateValue.From(new("axiom.bool", 1), true));
    }
}
