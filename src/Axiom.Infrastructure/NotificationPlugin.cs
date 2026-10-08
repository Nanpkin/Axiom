using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class NotificationPlugin : IAutomationPlugin
{
    public string Id => "axiom.notification";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new NotificationTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId, HotkeyCapabilities.Pressed,
        SystemEventCapabilities.Event, AudioCapabilities.Changed, ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent,
        IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, FileArrivalCapabilities.Arrived }.Select(d => new TaskCombination(d, NotificationCapabilities.Show))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(NotificationCapabilities.Show, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(20), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}
public sealed class NotificationTaskAction : ITaskActionCompiler
{
    public TaskCapability Descriptor => NotificationCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        NotificationCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "desktop");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(NotificationCapabilities.Validate(configuration).Delay));
}
