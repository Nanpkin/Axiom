using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class MicrophonePlugin : IAutomationPlugin
{
    public string Id => "axiom.microphone";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new MicrophoneTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId, HotkeyCapabilities.Pressed,
        SystemEventCapabilities.Event, AudioCapabilities.Changed, ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent,
        IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due }.Select(d => new TaskCombination(d, MicrophoneCapabilities.Set))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(MicrophoneCapabilities.Set, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}
public sealed class MicrophoneTaskAction : ITaskActionCompiler
{
    public TaskCapability Descriptor => MicrophoneCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        MicrophoneCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "default-communications-input");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(MicrophoneCapabilities.Validate(configuration).Delay));
}
