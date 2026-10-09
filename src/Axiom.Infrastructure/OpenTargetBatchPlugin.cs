using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class OpenTargetBatchPlugin : IAutomationPlugin
{
    public string Id => "axiom.open-target-batch";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new OpenTargetBatchTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { HotkeyCapabilities.Pressed, SystemEventCapabilities.Event,
        DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, WifiProfileCapabilities.Connected }.Select(d => new TaskCombination(d, OpenTargetBatchCapabilities.Open))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(OpenTargetBatchCapabilities.Open, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class OpenTargetBatchTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => OpenTargetBatchCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        OpenTargetBatchCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "work-target-batch");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(OpenTargetBatchCapabilities.Validate(configuration).Delay));
}
