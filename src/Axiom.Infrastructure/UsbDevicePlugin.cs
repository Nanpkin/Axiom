using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class UsbDevicePlugin : IAutomationPlugin
{
    public string Id => "axiom.usb-device";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new UsbDeviceTaskDetection(false), new UsbDeviceTaskDetection(true)];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State,
        TrayCapabilities.Hide, OpenTargetCapabilities.Open, NotificationCapabilities.Show, AudioPlaybackCapabilities.Set, MicrophoneCapabilities.Set, ApplicationAudioCapabilities.Set, PowerSchemeCapabilities.Set, WindowMonitorCapabilities.Move }
        .SelectMany(a => new[] { new TaskCombination(UsbDeviceCapabilities.Present, a), new TaskCombination(UsbDeviceCapabilities.Absent, a) }),
        new(UsbDeviceCapabilities.Present, KeepAwakeCapabilities.Hold)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(UsbDeviceCapabilities.Present, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class UsbDeviceTaskDetection(bool absent) : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => absent ? UsbDeviceCapabilities.AbsentDescriptor : UsbDeviceCapabilities.PresentDescriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var conditions = UsbDeviceCapabilities.Validate(configuration).Select(id => RuleCondition.Equal(
            new(UsbDeviceCapabilities.Present, id, scope), StateValue.From(new("axiom.bool", 1), !absent))).ToArray();
        return conditions.Length == 1 ? conditions[0] : absent ? RuleCondition.All(conditions) : RuleCondition.Any(conditions);
    }
}
