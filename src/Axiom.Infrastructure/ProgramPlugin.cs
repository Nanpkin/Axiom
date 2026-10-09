using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ProgramPlugin : IAutomationPlugin
{
    public string Id => "axiom.programs";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new ProcessRunningTaskDetection(), new ProcessExitedTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [new ProgramTaskAction(true), new ProgramTaskAction(false)];
    public ImmutableArray<TaskCombination> Combinations => [new("process.running", "program.start"), new("process.running", "program.close"),
        new("process.exited", "program.start"), new("process.exited", "program.close")];
    public IEnumerable<CapabilityBinding> Bindings => [Binding("process.running", true), Binding("program.start", false), Binding("program.close", false)];
    private static CapabilityBinding Binding(string id, bool observe) => new(new(id,
        observe ? new("axiom.bool", 1) : TaskInvocationCommands.Schema, new("axiom.bool", 1), ScopeKind.Session, observe, !observe, false,
        ExecutionLocation.SessionAgent, "same-user-session", true, "available", false, TimeSpan.FromSeconds(40), true,
        ConcurrencyScope.Resource, RetryCategory.Never, observe ? ConfirmationSource.Observation : ConfirmationSource.ReceiptOnly,
        EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "same user", []),
        observe ? ScalarKind.Boolean : ScalarKind.Text, ScalarKind.Boolean);
}
public sealed class ProcessExitedTaskDetection : ITaskDetectionCompiler
{
    private readonly ProcessRunningTaskDetection running = new();
    public TaskCapability Descriptor => running.Descriptor with { Id = "process.exited", Name = "程序全部退出",
        Description = "指定程序全部退出时条件成立。", Behavior = "最后一个匹配实例退出时成立；启动时已退出不会补执行。",
        Fields = [.. running.Descriptor.Fields.Select(f => f with { Hint = "所有选择的程序退出后成立，包含没有窗口的进程。仅检测当前用户会话。" })] };
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleCondition.Not(running.Compile(configuration with { CapabilityId = "process.running" }, scope));
    }
}
public sealed class ProgramTaskAction(bool start) : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor { get; } = ProgramCapabilities.Describe(start);
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var paths = ProgramCapabilities.ValidateTargets(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, ExecutablePaths.ResourceId(paths));
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds((double)ProgramCapabilities.Number(configuration, "delaySeconds")));
}
