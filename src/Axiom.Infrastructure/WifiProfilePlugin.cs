using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class WifiProfilePlugin : IAutomationPlugin
{
    public string Id => "axiom.wifi-profile";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new WifiProfileTaskDetection(false), new WifiProfileTaskDetection(true)];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State,
        TrayCapabilities.Hide, OpenTargetCapabilities.Open, NotificationCapabilities.Show, AudioPlaybackCapabilities.Set, MicrophoneCapabilities.Set, ApplicationAudioCapabilities.Set }
        .SelectMany(a => new[] { new TaskCombination(WifiProfileCapabilities.Connected, a), new TaskCombination(WifiProfileCapabilities.Disconnected, a) })];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(WifiProfileCapabilities.Connected, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class WifiProfileTaskDetection(bool disconnected) : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => disconnected ? WifiProfileCapabilities.DisconnectedDescriptor : WifiProfileCapabilities.ConnectedDescriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var conditions = WifiProfileCapabilities.Parse(configuration.Parameters["profileNames"]).Select(name => RuleCondition.Equal(
            new(WifiProfileCapabilities.Connected, name, scope), StateValue.From(new("axiom.bool", 1), !disconnected))).ToArray();
        return conditions.Length == 1 ? conditions[0] : disconnected ? RuleCondition.All(conditions) : RuleCondition.Any(conditions);
    }
}
