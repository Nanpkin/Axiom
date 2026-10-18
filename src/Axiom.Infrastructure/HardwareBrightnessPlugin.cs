using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class HardwareBrightnessPlugin : IAutomationPlugin
{
    public string Id => "axiom.hardware-brightness";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new HardwareBrightnessTaskAction(), new HardwareBrightnessAdjustTaskAction(), new RestorableSettingTaskAction(HardwareBrightnessRestoreCapabilities.Descriptor, HardwareBrightnessRestoreCapabilities.Validate, _ => "brightness")];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId,
        HotkeyCapabilities.Pressed, SystemEventCapabilities.Event, ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible,
        WindowPresenceCapabilities.Absent, WifiProfileCapabilities.Connected, WifiProfileCapabilities.Disconnected, UsbDeviceCapabilities.Present,
        UsbDeviceCapabilities.Absent, IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due, OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due, PowerStateCapabilities.State, PowerSchemeStateCapabilities.State, PowerSaverStateCapabilities.State }
        .SelectMany(d => new[] { new TaskCombination(d, HardwareBrightnessCapabilities.Set), new(d, HardwareBrightnessAdjustCapabilities.Adjust) })
        .Concat(RestoreConditions.Select(d => new TaskCombination(d, HardwareBrightnessRestoreCapabilities.Set)))];
    private static readonly string[] RestoreConditions = ["process.running", ForegroundCapabilities.Active, WindowPresenceCapabilities.Visible,
        WindowPresenceCapabilities.Absent, WindowStateDetectionCapabilities.StateIs, ForegroundScreenCoverCapabilities.Covers,
        PowerStateCapabilities.State, PowerSchemeStateCapabilities.State, PowerSaverStateCapabilities.State, WifiProfileCapabilities.Connected,
        WifiProfileCapabilities.Disconnected, UsbDeviceCapabilities.Present, UsbDeviceCapabilities.Absent, MediaPlaybackCapabilities.State];
    public IEnumerable<CapabilityBinding> Bindings => new[] { HardwareBrightnessCapabilities.Set, HardwareBrightnessAdjustCapabilities.Adjust }.Select(id => new CapabilityBinding(new(id, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(30), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)).Append(RestorableSettingTaskAction.Binding(HardwareBrightnessRestoreCapabilities.Set, TimeSpan.FromSeconds(40)));
}

public sealed class HardwareBrightnessTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => HardwareBrightnessCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        HardwareBrightnessCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "hardware-brightness-screens");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(HardwareBrightnessCapabilities.Validate(configuration).Delay));
}

public sealed class HardwareBrightnessAdjustTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => HardwareBrightnessAdjustCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        HardwareBrightnessAdjustCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "hardware-brightness-screens");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(HardwareBrightnessAdjustCapabilities.Validate(configuration).Delay));
}
