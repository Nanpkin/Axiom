using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class BuiltInBrightnessPlugin : IAutomationPlugin
{
    public string Id => "axiom.built-in-brightness";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new BuiltInBrightnessTaskAction(), new BuiltInBrightnessAdjustTaskAction(), new RestorableSettingTaskAction(BuiltInBrightnessRestoreCapabilities.Descriptor, BuiltInBrightnessRestoreCapabilities.Validate, _ => "brightness")];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId,
        HotkeyCapabilities.Pressed, SystemEventCapabilities.Event, ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible,
        WindowPresenceCapabilities.Absent, WifiProfileCapabilities.Connected, WifiProfileCapabilities.Disconnected, UsbDeviceCapabilities.Present,
        UsbDeviceCapabilities.Absent, IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due }
        .SelectMany(d => new[] { new TaskCombination(d, BuiltInBrightnessCapabilities.Set), new(d, BuiltInBrightnessAdjustCapabilities.Adjust) })
        .Concat(RestoreConditions.Select(d => new TaskCombination(d, BuiltInBrightnessRestoreCapabilities.Set)))];
    private static readonly string[] RestoreConditions = ["process.running", ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible,
        WindowPresenceCapabilities.Absent, WindowStateDetectionCapabilities.StateIs, ForegroundScreenCoverCapabilities.Covers,
        PowerStateCapabilities.State, PowerSchemeStateCapabilities.State, PowerSaverStateCapabilities.State, WifiProfileCapabilities.Connected,
        WifiProfileCapabilities.Disconnected, UsbDeviceCapabilities.Present, UsbDeviceCapabilities.Absent, MediaPlaybackCapabilities.State];
    public IEnumerable<CapabilityBinding> Bindings => new[] { BuiltInBrightnessCapabilities.Set, BuiltInBrightnessAdjustCapabilities.Adjust }.Select(id => new CapabilityBinding(new(id, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(30), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)).Append(RestorableSettingTaskAction.Binding(BuiltInBrightnessRestoreCapabilities.Set, TimeSpan.FromSeconds(40)));
}

public sealed class BuiltInBrightnessTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => BuiltInBrightnessCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        BuiltInBrightnessCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "supported-built-in-panels");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(BuiltInBrightnessCapabilities.Validate(configuration).Delay));
}

public sealed class BuiltInBrightnessAdjustTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => BuiltInBrightnessAdjustCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        BuiltInBrightnessAdjustCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "supported-built-in-panels");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(BuiltInBrightnessAdjustCapabilities.Validate(configuration).Delay));
}
