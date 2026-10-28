using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
namespace Axiom.Infrastructure;
public sealed class GameModePlugin(IEnumerable<string> actions) : IAutomationPlugin
{
    public string Id => "axiom.game-mode";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new GameModeTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. actions.Distinct().Select(a => new TaskCombination(GameModeCapabilities.State, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(GameModeCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1), ScopeKind.Session, true, false, false,
        ExecutionLocation.Service, "same-user-session", true, "available", false, TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never,
        ConfirmationSource.Observation, EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "game mode", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class GameModeTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => GameModeCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleCondition.Equal(new(GameModeCapabilities.State, "session", scope), StateValue.From(new("axiom.bool", 1), configuration.Parameters["state"] == "on"));
    }
}
