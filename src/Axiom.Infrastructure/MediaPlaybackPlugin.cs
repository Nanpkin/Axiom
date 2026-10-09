using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class MediaPlaybackPlugin : IAutomationPlugin
{
    public string Id => "axiom.media-playback";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new MediaPlaybackTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    // Media control, program close/start and synthetic input can change their own detection.
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { KeepAwakeCapabilities.Hold, AudioPlaybackCapabilities.Set,
        ApplicationAudioCapabilities.Set, AudioPlaybackAdjustCapabilities.Adjust, MicrophoneCapabilities.Set, MicrophoneToggleCapabilities.Toggle, NotificationCapabilities.Show }
        .Select(action => new TaskCombination(MediaPlaybackCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(MediaPlaybackCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active media state rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class MediaPlaybackTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => MediaPlaybackCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Equal(
        new(MediaPlaybackCapabilities.State, MediaPlaybackCapabilities.Validate(configuration), scope), StateValue.From(new("axiom.bool", 1), true));
}
