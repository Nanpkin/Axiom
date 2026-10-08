using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class AudioPlugin : IAutomationPlugin
{
    public string Id => "axiom.audio-events";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new AudioTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide, OpenTargetCapabilities.Open }
        .Select(a => new TaskCombination(AudioCapabilities.Changed, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(AudioCapabilities.Changed, new("axiom.event-count", 1), new("axiom.event-count", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active audio rules or explicit observation", "same user", []), ScalarKind.Number, ScalarKind.Number)];
}
public sealed class AudioTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => AudioCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Event(
        new(AudioCapabilities.Changed, AudioCapabilities.Validate(configuration), scope), new("axiom.event-count", 1));
}
