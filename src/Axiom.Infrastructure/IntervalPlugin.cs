using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class IntervalPlugin : IAutomationPlugin
{
    public string Id => "axiom.interval";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new IntervalTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide, OpenTargetCapabilities.Open }
        .Select(a => new TaskCombination(IntervalCapabilities.Elapsed, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(IntervalCapabilities.Elapsed, new("axiom.event-count", 1), new("axiom.event-count", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active interval rules", "same user", []), ScalarKind.Number, ScalarKind.Number)];
}
public sealed class IntervalTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => IntervalCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Event(
        new(IntervalCapabilities.Elapsed, IntervalCapabilities.Resource(IntervalCapabilities.Validate(configuration)), scope), new("axiom.event-count", 1));
}
