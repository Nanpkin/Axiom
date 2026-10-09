using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class WorkPreparationPlugin : IAutomationPlugin
{
    public string Id => "axiom.work-preparation";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new WorkPreparationTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { HotkeyCapabilities.Pressed, SystemEventCapabilities.Event,
        DailyTimeCapabilities.Due, OnceTimeCapabilities.Due }.Select(d => new TaskCombination(d, WorkPreparationCapabilities.Prepare))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(WorkPreparationCapabilities.Prepare, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class WorkPreparationTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => WorkPreparationCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        WorkPreparationCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "work-preparation");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(WorkPreparationCapabilities.Validate(configuration).Delay));
}
