using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class WindowPlugin : IAutomationPlugin
{
    public string Id => "axiom.windows";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new CloseWindowTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [new("process.running", WindowCapabilities.Close), new("process.exited", WindowCapabilities.Close), new(AxiomStartupPlugin.CapabilityId, WindowCapabilities.Close)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(WindowCapabilities.Close, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly,
        EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class CloseWindowTaskAction : ITaskActionCompiler
{
    public TaskCapability Descriptor => WindowCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope) =>
        TaskInvocationCommands.Compile(configuration, Descriptor, scope, ExecutablePaths.ResourceId(WindowCapabilities.ValidateTargets(configuration)));
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds((double)WindowCapabilities.Number(configuration, "delaySeconds")));
}
