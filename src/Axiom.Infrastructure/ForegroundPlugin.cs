using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ForegroundPlugin : IAutomationPlugin
{
    public string Id => "axiom.foreground";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new ForegroundTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide, OpenTargetCapabilities.Open }
        .Select(a => new TaskCombination(ForegroundCapabilities.Active, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(ForegroundCapabilities.Active, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules or explicit observation", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class ForegroundTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => ForegroundCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var conditions = ProcessNameSelection.Parse(configuration.Parameters["processNames"]).Select(name => RuleCondition.Equal(new(ForegroundCapabilities.Active, name, scope), StateValue.From(new("axiom.bool", 1), true))).ToArray();
        return conditions.Length == 1 ? conditions[0] : RuleCondition.Any(conditions);
    }
}
