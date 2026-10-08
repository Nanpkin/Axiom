using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class WindowPresencePlugin : IAutomationPlugin
{
    public string Id => "axiom.window-presence";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new WindowPresenceTaskDetection(), new WindowAbsenceTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide, OpenTargetCapabilities.Open }
        .SelectMany(a => new[] { new TaskCombination(WindowPresenceCapabilities.Visible, a), new TaskCombination(WindowPresenceCapabilities.Absent, a) })];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(WindowPresenceCapabilities.Visible, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules or explicit observation", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class WindowPresenceTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => WindowPresenceCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var conditions = ProcessNameSelection.Parse(configuration.Parameters["processNames"]).Select(name => RuleCondition.Equal(new(WindowPresenceCapabilities.Visible, name, scope), StateValue.From(new("axiom.bool", 1), true))).ToArray();
        return conditions.Length == 1 ? conditions[0] : RuleCondition.Any(conditions);
    }
}

// Both user-facing detections consume the same native presence resources and subscription.
public sealed class WindowAbsenceTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => WindowPresenceCapabilities.AbsentDescriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var conditions = ProcessNameSelection.Parse(configuration.Parameters["processNames"]).Select(name => RuleCondition.Equal(new(WindowPresenceCapabilities.Visible, name, scope), StateValue.From(new("axiom.bool", 1), false))).ToArray();
        return conditions.Length == 1 ? conditions[0] : RuleCondition.All(conditions);
    }
}
