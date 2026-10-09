using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ApplicationAudioActivityPlugin : IAutomationPlugin
{
    public string Id => "axiom.application-audio-activity";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new ApplicationAudioActivityTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [new(ApplicationAudioActivityCapabilities.Active, KeepAwakeCapabilities.Hold),
        new(ApplicationAudioActivityCapabilities.Active, NotificationCapabilities.Show)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(ApplicationAudioActivityCapabilities.Active, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active application audio rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class ApplicationAudioActivityTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => ApplicationAudioActivityCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        var conditions = ApplicationAudioActivityCapabilities.Validate(configuration).Select(path => RuleCondition.Equal(
            new(ApplicationAudioActivityCapabilities.Active, path.ToLowerInvariant(), scope), StateValue.From(new("axiom.bool", 1), true))).ToArray();
        return conditions.Length == 1 ? conditions[0] : RuleCondition.Any(conditions);
    }
}
