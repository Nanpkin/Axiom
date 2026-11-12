using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class TrayPlugin : IAutomationPlugin
{
    public string Id => "axiom.tray";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new HideTrayTaskAction(), new ShowTrayTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { TrayCapabilities.Hide, TrayCapabilities.Show }.SelectMany(a =>
        new[] { new TaskCombination("process.running", a), new TaskCombination("process.exited", a), new TaskCombination(AxiomStartupPlugin.CapabilityId, a) })];
    public IEnumerable<CapabilityBinding> Bindings => new[] { TrayCapabilities.Hide, TrayCapabilities.Show }.Select(id => new CapabilityBinding(new(id, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(TrayCapabilities.MaximumWaitSeconds + 10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly,
        EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean));
}

public sealed class ShowTrayTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => TrayCapabilities.ShowDescriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope) =>
        TaskInvocationCommands.Compile(configuration, Descriptor, scope, ExecutablePaths.ResourceId(TrayCapabilities.ValidateTargets(configuration)));
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds((double)TrayCapabilities.Number(configuration, "delaySeconds")));
}

public sealed class HideTrayTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => TrayCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope) =>
        TaskInvocationCommands.Compile(configuration, Descriptor, scope, ExecutablePaths.ResourceId(TrayCapabilities.ValidateTargets(configuration)));
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds((double)TrayCapabilities.Number(configuration, "delaySeconds")));
}
