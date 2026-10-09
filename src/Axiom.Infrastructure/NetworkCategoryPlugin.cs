using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class NetworkCategoryPlugin : IAutomationPlugin
{
    public string Id => "axiom.network-category";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new NetworkCategoryTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { NotificationCapabilities.Show, "program.start",
        OpenTargetCapabilities.Open, AudioPlaybackCapabilities.Set, KeepAwakeCapabilities.Hold }
        .Select(action => new TaskCombination(NetworkCategoryCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(NetworkCategoryCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active network category rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class NetworkCategoryTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => NetworkCategoryCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Equal(
        new(NetworkCategoryCapabilities.State, NetworkCategoryCapabilities.Validate(configuration), scope), StateValue.From(new("axiom.bool", 1), true));
}
