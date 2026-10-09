using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class KeepAwakePlugin : IAutomationPlugin
{
    public string Id => "axiom.keep-awake";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new KeepAwakeTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [new("process.running", KeepAwakeCapabilities.Hold),
        new(WindowPresenceCapabilities.Visible, KeepAwakeCapabilities.Hold), new(ForegroundCapabilities.Active, KeepAwakeCapabilities.Hold)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(KeepAwakeCapabilities.Hold, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, true, ExecutionLocation.SessionAgent, "same-user-session", true, "available", true,
        TimeSpan.FromSeconds(5), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active condition", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class KeepAwakeTaskAction : ITaskActionCompiler
{
    public TaskCapability Descriptor => KeepAwakeCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleAction.Desired(new(KeepAwakeCapabilities.Hold, KeepAwakeCapabilities.Validate(configuration), scope),
            StateValue.From(new("axiom.bool", 1), true));
    }
}
