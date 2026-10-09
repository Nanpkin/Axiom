using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class SessionLockPlugin : IAutomationPlugin
{
    public string Id => "axiom.session-lock";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new SessionLockTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.exited", HotkeyCapabilities.Pressed,
        WindowPresenceCapabilities.Absent, WifiProfileCapabilities.Disconnected, UsbDeviceCapabilities.Absent,
        IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due }
        .Select(d => new TaskCombination(d, SessionLockCapabilities.Lock))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(SessionLockCapabilities.Lock, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class SessionLockTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => SessionLockCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        SessionLockCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "current-session-lock");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(SessionLockCapabilities.Validate(configuration)));
}
