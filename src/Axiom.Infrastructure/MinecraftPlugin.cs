using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class MinecraftPlugin : IAutomationPlugin, ITaskDetectionCompiler, ITaskActionCompiler
{
    public string Id => "axiom.minecraft";
    public IEnumerable<ITaskDetectionCompiler> Detections => [this];
    public IEnumerable<ITaskActionCompiler> Actions => [this];
    public ImmutableArray<TaskCombination> Combinations => [new(MinecraftTool.Launch, MinecraftTool.Apply)];
    public IEnumerable<CapabilityBinding> Bindings => [Binding(true), Binding(false)];
    private static CapabilityBinding Binding(bool observe) => new(new(observe ? MinecraftTool.Launch : MinecraftTool.Apply,
        observe ? new("axiom.event-count", 1) : TaskInvocationCommands.Schema, observe ? new("axiom.event-count", 1) : new("axiom.bool", 1),
        ScopeKind.Session, observe, !observe, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(30), true, ConcurrencyScope.Resource, RetryCategory.Never, observe ? ConfirmationSource.Observation : ConfirmationSource.ReceiptOnly,
        EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "enabled launcher tool", "local driver", []),
        observe ? ScalarKind.Number : ScalarKind.Text, observe ? ScalarKind.Number : ScalarKind.Boolean);
    TaskCapability ITaskDetectionCompiler.Descriptor => MinecraftTool.Detection;
    TaskCapability ITaskActionCompiler.Descriptor => MinecraftTool.Action;
    RuleCondition ITaskDetectionCompiler.Compile(TaskInvocation invocation, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(invocation, MinecraftTool.Detection);
        var launcher = invocation.Parameters["launcher"];
        if (!ProcessGroups.ValidProcessName(launcher)) throw new ArgumentException("启动器文件名无效。");
        return RuleCondition.Event(new(MinecraftTool.Launch, launcher.ToLowerInvariant(), scope), new("axiom.event-count", 1));
    }
    public RuleAction Compile(TaskInvocation invocation, ResourceScope scope)
    {
        if (invocation.Parameters.TryGetValue("javaPath", out var path) && path.Length > 0) MinecraftTool.ValidateJavaPath(path);
        return TaskInvocationCommands.Compile(invocation, MinecraftTool.Action, scope, "javaw.exe");
    }
    public RuleTiming Timing(TaskInvocation invocation) => new();
}
