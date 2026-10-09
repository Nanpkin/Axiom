using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ForegroundWindowLayoutPlugin : IAutomationPlugin
{
    public string Id => "axiom.foreground-window-layout";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new ForegroundWindowLayoutTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [new(HotkeyCapabilities.Pressed, ForegroundWindowLayoutCapabilities.Layout)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(ForegroundWindowLayoutCapabilities.Layout, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class ForegroundWindowLayoutTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => ForegroundWindowLayoutCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        ForegroundWindowLayoutCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "current-foreground-window");
    }
}
