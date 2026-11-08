using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class BluetoothDevicePlugin : IAutomationPlugin
{
    public string Id => "axiom.bluetooth-device";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new BluetoothDeviceTaskDetection(false), new BluetoothDeviceTaskDetection(true)];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { TrayCapabilities.Hide, TrayCapabilities.Show, "program.start", "program.close", NotificationCapabilities.Show }
        .SelectMany(a => new[] { new TaskCombination(BluetoothDeviceCapabilities.Connected, a), new TaskCombination(BluetoothDeviceCapabilities.Disconnected, a) })];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(BluetoothDeviceCapabilities.Connected, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class BluetoothDeviceTaskDetection(bool disconnected) : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => disconnected ? BluetoothDeviceCapabilities.DisconnectedDescriptor : BluetoothDeviceCapabilities.ConnectedDescriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var conditions = BluetoothDeviceCapabilities.Validate(configuration).Select(name => RuleCondition.Equal(
            new(BluetoothDeviceCapabilities.Connected, name, scope), StateValue.From(new("axiom.bool", 1), !disconnected))).ToArray();
        return conditions.Length == 1 ? conditions[0] : disconnected ? RuleCondition.All(conditions) : RuleCondition.Any(conditions);
    }
}
