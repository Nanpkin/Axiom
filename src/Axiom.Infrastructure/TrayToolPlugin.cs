using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

// Internal toolbox projection. It reuses the host lifecycle input and the unchanged tray command.
public sealed class TrayToolPlugin : IAutomationPlugin
{
    public string Id => "axiom.tray-tool";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new TrayToolDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [new(TrayTool.Trigger, TrayCapabilities.Hide)];
    public IEnumerable<CapabilityBinding> Bindings => [];
}
public sealed class TrayToolDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor { get; } = new(TrayTool.Trigger, 1, TaskCapabilityKind.Detection, "工具箱", TrayTool.Name, "由托盘工具管理的启动和通知输入。",
        [new("frequency", "启动次数", TaskFieldKind.Choice, true, "", "every", 16, [new("every", "每次后台启动")]),
         new("mode", "隐藏模式", TaskFieldKind.Choice, true, "", TrayTool.Startup, 16, [new(TrayTool.Startup, "仅启动时"), new(TrayTool.Continuous, "持续隐藏")])], "", "");
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => throw new ArgumentException("工具检测需要任务标识。");
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope, string taskId)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        if (taskId != TrayTool.TaskId) throw new ArgumentException("工具检测只能由托盘工具使用。");
        return RuleCondition.Equal(new(AxiomStartupPlugin.CapabilityId, taskId, scope), StateValue.From(new("axiom.bool", 1), true));
    }
}
