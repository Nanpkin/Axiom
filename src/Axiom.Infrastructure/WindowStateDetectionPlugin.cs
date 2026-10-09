using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class WindowStateDetectionPlugin : IAutomationPlugin
{
    public string Id => "axiom.window-state-detection";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new WindowStateTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close,
        OpenTargetCapabilities.Open, NotificationCapabilities.Show, KeepAwakeCapabilities.Hold }
        .Select(action => new TaskCombination(WindowStateDetectionCapabilities.StateIs, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(WindowStateDetectionCapabilities.StateIs, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active window state rules", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}

public sealed class WindowStateTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => WindowStateDetectionCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        var (paths, state) = WindowStateDetectionCapabilities.Validate(configuration);
        var conditions = paths.Select(path => RuleCondition.Equal(new(WindowStateDetectionCapabilities.StateIs,
            WindowStateDetectionCapabilities.Resource(path, state), scope), StateValue.From(new("axiom.bool", 1), true))).ToArray();
        return conditions.Length == 1 ? conditions[0] : RuleCondition.Any(conditions);
    }
}
