using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class MicrophoneTogglePlugin : IAutomationPlugin
{
    public string Id => "axiom.microphone-toggle";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new MicrophoneToggleTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId, HotkeyCapabilities.Pressed,
        SystemEventCapabilities.Event, AudioCapabilities.Changed, ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent,
        IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due }.Select(d => new TaskCombination(d, MicrophoneToggleCapabilities.Toggle))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(MicrophoneToggleCapabilities.Toggle, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class MicrophoneToggleTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => MicrophoneToggleCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        MicrophoneToggleCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "default-communications-input");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(MicrophoneToggleCapabilities.Validate(configuration)));
}
