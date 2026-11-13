using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class SunshinePlugin : IAutomationPlugin
{
    public string Id => "axiom.sunshine";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new SunshineTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { NotificationCapabilities.Show, SystemSoundCapabilities.Sound,
        KeepAwakeCapabilities.Hold, TrayCapabilities.Hide, TrayCapabilities.Show, "program.start", "program.close",
        BuiltInBrightnessCapabilities.Set, HardwareBrightnessCapabilities.Set, BuiltInBrightnessRestoreCapabilities.Set, HardwareBrightnessRestoreCapabilities.Set }
        .Select(action => new TaskCombination(SunshineCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(SunshineCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active Sunshine rules", "local Sunshine", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class SunshineTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => SunshineCapabilities.Descriptor;

    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Equal(
        new(SunshineCapabilities.State, SunshineCapabilities.Validate(configuration), scope),
        StateValue.From(new("axiom.bool", 1), true));
}
