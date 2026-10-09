using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class SessionDisplayPlugin : IAutomationPlugin
{
    public string Id => "axiom.session-display";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new SessionDisplayTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { AudioPlaybackCapabilities.Set, MicrophoneCapabilities.Set,
        NotificationCapabilities.Show, "program.close" }.Select(action => new TaskCombination(SessionDisplayCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(SessionDisplayCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active screen state rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class SessionDisplayTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => SessionDisplayCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleCondition.Equal(new(SessionDisplayCapabilities.State, SessionDisplayCapabilities.Validate(configuration), scope), StateValue.From(new("axiom.bool", 1), true));
    }
}
