using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class PowerSaverStatePlugin : IAutomationPlugin
{
    public string Id => "axiom.power-saver-state";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new PowerSaverStateTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State,
        TrayCapabilities.Hide, OpenTargetCapabilities.Open, NotificationCapabilities.Show, AudioPlaybackCapabilities.Set, MicrophoneCapabilities.Set,
        ApplicationAudioCapabilities.Set, AudioPlaybackAdjustCapabilities.Adjust, MicrophoneToggleCapabilities.Toggle, OpenTargetBatchCapabilities.Open, WindowMonitorCapabilities.Move, BuiltInBrightnessCapabilities.Set }
        .Select(a => new TaskCombination(PowerSaverStateCapabilities.State, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(PowerSaverStateCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class PowerSaverStateTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => PowerSaverStateCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleCondition.Equal(new(PowerSaverStateCapabilities.State, PowerSaverStateCapabilities.Validate(configuration), scope), StateValue.From(new("axiom.bool", 1), true));
    }
}
