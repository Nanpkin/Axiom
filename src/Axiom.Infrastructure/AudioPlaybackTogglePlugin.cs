using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class AudioPlaybackTogglePlugin : IAutomationPlugin
{
    public string Id => "axiom.audio-playback-toggle";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new AudioPlaybackToggleTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { HotkeyCapabilities.Pressed, SystemEventCapabilities.Event,
        DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due }.Select(d => new TaskCombination(d, AudioPlaybackToggleCapabilities.Toggle))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(AudioPlaybackToggleCapabilities.Toggle, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class AudioPlaybackToggleTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => AudioPlaybackToggleCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AudioPlaybackToggleCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "default-playback");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(AudioPlaybackToggleCapabilities.Validate(configuration)));
}
