using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class PowerSchemePlugin : IAutomationPlugin
{
    public string Id => "axiom.power-scheme";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new PowerSchemeTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId,
        HotkeyCapabilities.Pressed, SystemEventCapabilities.Event, AudioCapabilities.Changed, ForegroundCapabilities.Active,
        WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent, WifiProfileCapabilities.Connected, WifiProfileCapabilities.Disconnected,
        IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due, FileArrivalCapabilities.Arrived }
        .Select(d => new TaskCombination(d, PowerSchemeCapabilities.Set))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(PowerSchemeCapabilities.Set, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class PowerSchemeTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => PowerSchemeCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        PowerSchemeCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "active-power-scheme");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(PowerSchemeCapabilities.Validate(configuration).Delay));
}
