using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class DailyTimePlugin : IAutomationPlugin
{
    public string Id => "axiom.daily-time";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new DailyTimeTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide, OpenTargetCapabilities.Open }
        .Select(a => new TaskCombination(DailyTimeCapabilities.Due, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(DailyTimeCapabilities.Due, new("axiom.event-count", 1), new("axiom.event-count", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active calendar rules", "same user", []), ScalarKind.Number, ScalarKind.Number)];
}
public sealed class DailyTimeTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => DailyTimeCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Event(
        new(DailyTimeCapabilities.Due, DailyTimeCapabilities.Validate(configuration).Resource, scope), new("axiom.event-count", 1));
}
