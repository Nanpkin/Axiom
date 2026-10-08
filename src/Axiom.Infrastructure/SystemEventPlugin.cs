using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class SystemEventPlugin : IAutomationPlugin
{
    public string Id => "axiom.system-events";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new SystemEventTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    // Keyboard injection on a locked desktop is deliberately not an offered combination.
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide }
        .Select(a => new TaskCombination(SystemEventCapabilities.Event, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(SystemEventCapabilities.Event, new("axiom.event-count", 1), new("axiom.event-count", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules or explicit observation", "same user", []), ScalarKind.Number, ScalarKind.Number)];
}
public sealed class SystemEventTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => SystemEventCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Event(
        new(SystemEventCapabilities.Event, SystemEventCapabilities.Validate(configuration), scope), new("axiom.event-count", 1));
}
