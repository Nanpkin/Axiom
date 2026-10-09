using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ProgramPriorityPlugin : IAutomationPlugin
{
    public string Id => "axiom.program-priority";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new ProgramPriorityTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId,
        HotkeyCapabilities.Pressed, SystemEventCapabilities.Event, AudioCapabilities.Changed, ForegroundCapabilities.Active,
        WindowPresenceCapabilities.Visible, WindowPresenceCapabilities.Absent, IntervalCapabilities.Elapsed, DailyTimeCapabilities.Due,
        OnceTimeCapabilities.Due, WindowedIntervalCapabilities.Due, FileArrivalCapabilities.Arrived }.Select(d => new TaskCombination(d, ProgramPriorityCapabilities.Set))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(ProgramPriorityCapabilities.Set, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}

public sealed class ProgramPriorityTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => ProgramPriorityCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        var settings = ProgramPriorityCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, ExecutablePaths.ResourceId(settings.Paths));
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(ProgramPriorityCapabilities.Validate(configuration).Delay));
}
