using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class TaskbarAutoHidePlugin : IAutomationPlugin
{
    public string Id => "axiom.taskbar-auto-hide";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new TaskbarAutoHideTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { HotkeyCapabilities.Pressed, "process.running", "process.exited",
        ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent,
        IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due,
        SystemEventCapabilities.Event }.Select(d => new TaskCombination(d, TaskbarAutoHideCapabilities.Set))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(TaskbarAutoHideCapabilities.Set, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}
public sealed class TaskbarAutoHideTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => TaskbarAutoHideCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        TaskbarAutoHideCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "system-taskbar");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(TaskbarAutoHideCapabilities.Validate(configuration).Delay));
}
