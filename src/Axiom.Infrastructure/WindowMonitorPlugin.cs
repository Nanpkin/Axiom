using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class WindowMonitorPlugin : IAutomationPlugin
{
    public string Id => "axiom.window-monitor";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new WindowMonitorTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId,
        HotkeyCapabilities.Pressed, SystemEventCapabilities.Event, ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible,
        AudioCapabilities.Changed, WifiProfileCapabilities.Connected }.Select(d => new TaskCombination(d, WindowMonitorCapabilities.Move))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(WindowMonitorCapabilities.Move, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly,
        EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class WindowMonitorTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => WindowMonitorCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope) => TaskInvocationCommands.Compile(configuration, Descriptor, scope,
        ExecutablePaths.ResourceId(WindowMonitorCapabilities.ValidateTargets(configuration)));
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds((double)WindowMonitorCapabilities.Number(configuration, "delaySeconds")));
}
