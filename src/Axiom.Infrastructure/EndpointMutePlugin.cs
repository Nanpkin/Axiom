using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class EndpointMutePlugin : IAutomationPlugin
{
    public string Id => "axiom.endpoint-mute";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new EndpointMuteTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    // Exclude endpoint mute setters: a rule that reverses its own condition would create feedback.
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { KeepAwakeCapabilities.Hold, NotificationCapabilities.Show,
        "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, WindowMonitorCapabilities.Move, OpenTargetCapabilities.Open }
        .Select(action => new TaskCombination(EndpointMuteCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(EndpointMuteCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active endpoint mute rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class EndpointMuteTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => EndpointMuteCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        var (endpoint, muted) = EndpointMuteCapabilities.Validate(configuration);
        return RuleCondition.Equal(new(EndpointMuteCapabilities.State, endpoint, scope), StateValue.From(new("axiom.bool", 1), muted));
    }
}
