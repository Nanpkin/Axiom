using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class OpenTargetPlugin : IAutomationPlugin
{
    public string Id => "axiom.open-target";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new OpenTargetTaskAction()];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "process.running", "process.exited", AxiomStartupPlugin.CapabilityId, HotkeyCapabilities.Pressed, SystemEventCapabilities.Event }.Select(d => new TaskCombination(d, OpenTargetCapabilities.Open))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(OpenTargetCapabilities.Open, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active commands", "same user", []), ScalarKind.Text, ScalarKind.Boolean)];
}
public sealed class OpenTargetTaskAction : ITaskActionCompiler
{
    public TaskCapability Descriptor => OpenTargetCapabilities.Descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        var target = OpenTargetCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, target.Kind + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Target))));
    }
    public RuleTiming Timing(TaskInvocation configuration) => new(minimumDuration: TimeSpan.FromSeconds(OpenTargetCapabilities.Validate(configuration).Delay));
}
