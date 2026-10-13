using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class SessionLockStatePlugin : IAutomationPlugin
{
    public string Id => "axiom.session-lock-state";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new SessionLockStateTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { KeepAwakeCapabilities.Hold, NotificationCapabilities.Show, SystemSoundCapabilities.Sound,
        AudioPlaybackCapabilities.Set, ApplicationAudioCapabilities.Set }.Select(a => new TaskCombination(SessionLockStateCapabilities.State, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(SessionLockStateCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active lock state rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class SessionLockStateTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => SessionLockStateCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Equal(
        new(SessionLockStateCapabilities.State, SessionLockStateCapabilities.Resource, scope),
        StateValue.From(new("axiom.bool", 1), SessionLockStateCapabilities.Validate(configuration) == "locked"));
}
