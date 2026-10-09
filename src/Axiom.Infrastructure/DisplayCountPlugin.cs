using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class DisplayCountPlugin : IAutomationPlugin
{
    public string Id => "axiom.display-count";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new DisplayCountTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.State,
        WindowMonitorCapabilities.Move, OpenTargetCapabilities.Open, NotificationCapabilities.Show, KeepAwakeCapabilities.Hold }
        .Select(action => new TaskCombination(DisplayCountCapabilities.Count, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(DisplayCountCapabilities.Count, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active display count rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class DisplayCountTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => DisplayCountCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleCondition.Equal(new(DisplayCountCapabilities.Count, DisplayCountCapabilities.Validate(configuration), scope), StateValue.From(new("axiom.bool", 1), true));
    }
}
