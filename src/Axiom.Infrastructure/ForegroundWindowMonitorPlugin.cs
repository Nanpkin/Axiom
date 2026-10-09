using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ForegroundWindowMonitorPlugin : IAutomationPlugin
{
    public string Id => "axiom.foreground-window-monitor";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new ForegroundWindowMonitorTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [new(HotkeyCapabilities.Pressed, ForegroundWindowMonitorCapabilities.Move)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(ForegroundWindowMonitorCapabilities.Move, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class ForegroundWindowMonitorTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => ForegroundWindowMonitorCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        ForegroundWindowMonitorCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "current-foreground-window");
    }
}
