using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class FileArrivalPlugin : IAutomationPlugin
{
    public string Id => "axiom.file-arrival";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new FileArrivalTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide, OpenTargetCapabilities.Open }
        .Select(a => new TaskCombination(FileArrivalCapabilities.Arrived, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(FileArrivalCapabilities.Arrived, new("axiom.event-count", 1), new("axiom.event-count", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active directory subscriptions", "same user", []), ScalarKind.Number, ScalarKind.Number)];
}
public sealed class FileArrivalTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => FileArrivalCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Event(
        new(FileArrivalCapabilities.Arrived, FileArrivalCapabilities.Resource(FileArrivalCapabilities.Validate(configuration)), scope), new("axiom.event-count", 1));
}
