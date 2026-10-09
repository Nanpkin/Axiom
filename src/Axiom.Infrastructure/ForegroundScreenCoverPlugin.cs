using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ForegroundScreenCoverPlugin : IAutomationPlugin
{
    public string Id => "axiom.foreground-screen-cover";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new ForegroundScreenCoverTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { KeepAwakeCapabilities.Hold, NotificationCapabilities.Show,
        SystemSoundCapabilities.Sound, ApplicationAudioCapabilities.Set, AudioPlaybackCapabilities.Set, AudioPlaybackAdjustCapabilities.Adjust }
        .Select(action => new TaskCombination(ForegroundScreenCoverCapabilities.Covers, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(ForegroundScreenCoverCapabilities.Covers, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active foreground cover rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class ForegroundScreenCoverTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => ForegroundScreenCoverCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        ForegroundScreenCoverCapabilities.Validate(configuration);
        return RuleCondition.Equal(new(ForegroundScreenCoverCapabilities.Covers, ForegroundScreenCoverCapabilities.Resource, scope), StateValue.From(new("axiom.bool", 1), true));
    }
}
