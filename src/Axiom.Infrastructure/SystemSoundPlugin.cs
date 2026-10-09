using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class SystemSoundPlugin : IAutomationPlugin
{
    public string Id => "axiom.system-sound";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new SystemSoundTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId,
        HotkeyCapabilities.Pressed, SystemEventCapabilities.Event, AudioCapabilities.Changed, ForegroundCapabilities.Active,
        WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent, IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due,
        OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due, FileArrivalCapabilities.Arrived, NetworkCategoryCapabilities.State, PowerSchemeStateCapabilities.State, PowerSaverStateCapabilities.State, SessionPresenceCapabilities.State }.Select(d => new TaskCombination(d, SystemSoundCapabilities.Sound))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(SystemSoundCapabilities.Sound, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class SystemSoundTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => SystemSoundCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        SystemSoundCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "system-sound");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(SystemSoundCapabilities.Validate(configuration).Delay));
}
