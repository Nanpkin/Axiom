using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class WindowOncePlugin : IAutomationPlugin
{
    public string Id => "axiom.window-once";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new WindowOnceTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State,
        TrayCapabilities.Hide, OpenTargetCapabilities.Open, OpenTargetBatchCapabilities.Open, WorkPreparationCapabilities.Prepare,
        NotificationCapabilities.Show, SystemSoundCapabilities.Sound, AudioPlaybackCapabilities.Set,
        AudioPlaybackToggleCapabilities.Toggle, MicrophoneCapabilities.Set, MicrophoneToggleCapabilities.Toggle, ApplicationAudioCapabilities.Set,
        ProgramPriorityCapabilities.Set, CurrentMediaCapabilities.Control, PowerSchemeCapabilities.Set, BuiltInBrightnessCapabilities.Set,
        BuiltInBrightnessAdjustCapabilities.Adjust, HardwareBrightnessCapabilities.Set, HardwareBrightnessAdjustCapabilities.Adjust,
        SessionLockCapabilities.Lock, TaskbarAutoHideCapabilities.Set }.Select(a => new TaskCombination(WindowOnceCapabilities.Due, a))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(WindowOnceCapabilities.Due, new("axiom.event-count", 1), new("axiom.event-count", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active window rules", "same user", []), ScalarKind.Number, ScalarKind.Number)];
}

public sealed class WindowOnceTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => WindowOnceCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => throw new ArgumentException("时段执行检测需要任务标识。");
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope, string taskId) => RuleCondition.Event(
        new(WindowOnceCapabilities.Due, WindowOnceCapabilities.Resource(taskId, WindowOnceCapabilities.Validate(configuration)), scope), new("axiom.event-count", 1));
}
