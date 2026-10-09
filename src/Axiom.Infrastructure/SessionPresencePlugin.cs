using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class SessionPresencePlugin : IAutomationPlugin
{
    public string Id => "axiom.session-presence";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new SessionPresenceTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { AudioPlaybackCapabilities.Set, NotificationCapabilities.Show }.Select(action => new TaskCombination(SessionPresenceCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(SessionPresenceCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active session presence rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class SessionPresenceTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => SessionPresenceCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleCondition.Equal(new(SessionPresenceCapabilities.State, SessionPresenceCapabilities.Validate(configuration), scope), StateValue.From(new("axiom.bool", 1), true));
    }
}
