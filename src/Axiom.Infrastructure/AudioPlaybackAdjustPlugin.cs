using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class AudioPlaybackAdjustPlugin : IAutomationPlugin
{
    public string Id => "axiom.audio-playback-adjust";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new AudioPlaybackAdjustTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [new(HotkeyCapabilities.Pressed, AudioPlaybackAdjustCapabilities.Adjust),
        new(WindowedIntervalCapabilities.Due, AudioPlaybackAdjustCapabilities.Adjust)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(AudioPlaybackAdjustCapabilities.Adjust, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class AudioPlaybackAdjustTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => AudioPlaybackAdjustCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AudioPlaybackAdjustCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "default-playback");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(AudioPlaybackAdjustCapabilities.Validate(configuration).Delay));
}
