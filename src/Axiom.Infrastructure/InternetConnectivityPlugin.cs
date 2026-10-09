using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class InternetConnectivityPlugin : IAutomationPlugin
{
    public string Id => "axiom.internet-connectivity";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new InternetConnectivityTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", OpenTargetCapabilities.Open, NotificationCapabilities.Show, KeepAwakeCapabilities.Hold }
        .Select(action => new TaskCombination(InternetConnectivityCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(InternetConnectivityCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active internet-state rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class InternetConnectivityTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => InternetConnectivityCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Equal(
        new(InternetConnectivityCapabilities.State, InternetConnectivityCapabilities.Resource, scope),
        StateValue.From(new("axiom.bool", 1), InternetConnectivityCapabilities.Validate(configuration)));
}
