using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class KeyboardPlugin : IAutomationPlugin
{
    public string Id => "axiom.keyboard";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new HotkeyTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [new SendKeysTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [
        .. new[] { "program.start", "program.close", WindowCapabilities.Close, TrayCapabilities.Hide, HotkeyCapabilities.Send }.Select(a => new TaskCombination(HotkeyCapabilities.Pressed, a)),
        .. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId }.Select(d => new TaskCombination(d, HotkeyCapabilities.Send))];
    public IEnumerable<CapabilityBinding> Bindings => [
        new(new(HotkeyCapabilities.Pressed, new("axiom.event-count", 1), new("axiom.event-count", 1), ScopeKind.Session, true, false, false,
            ExecutionLocation.SessionAgent, "same-user-session", true, "available", false, TimeSpan.FromSeconds(40), true,
            ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
            TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active rules", "same user", []), ScalarKind.Number, ScalarKind.Number),
        new(new(HotkeyCapabilities.Send, TaskInvocationCommands.Schema, new("axiom.bool", 1), ScopeKind.Session, false, true, false,
            ExecutionLocation.SessionAgent, "same-user-session", true, "available", false, TimeSpan.FromSeconds(10), true,
            ConcurrencyScope.Capability, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
            TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}
public sealed class HotkeyTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => HotkeyCapabilities.Detection;
    public RuleCondition Compile(TaskInvocation config, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(config, Descriptor);
        return RuleCondition.Event(new(HotkeyCapabilities.Pressed, HotkeyCapabilities.ResourceId(HotkeyCapabilities.Validate(config)), scope), new("axiom.event-count", 1));
    }
}
public sealed class SendKeysTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => HotkeyCapabilities.Action;
    public RuleAction Compile(TaskInvocation config, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(config, Descriptor); var chords = HotkeyCapabilities.Validate(config); HotkeyCapabilities.Delay(config);
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", chords.Select(c => c.ToString())))));
        return TaskInvocationCommands.Compile(config, Descriptor, scope, "keys:" + identity);
    }
    public RuleTiming Timing(TaskInvocation config) => new(minimumDuration: TimeSpan.FromSeconds(HotkeyCapabilities.Delay(config)));
}
