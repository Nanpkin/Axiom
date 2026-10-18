using System.Globalization;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class RestorableSettingTaskAction(TaskCapability descriptor, Action<TaskInvocation> validate,
    Func<TaskInvocation, string> resourceId) : ITaskActionCompiler
{
    public TaskCapability Descriptor => descriptor;
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor); validate(configuration);
        return RuleAction.Desired(new(Descriptor.Id, resourceId(configuration), scope),
            StateValue.From(RestorableSettingInvocations.Schema, RestorableSettingInvocations.Encode(configuration)));
    }
    public RuleTiming Timing(TaskInvocation configuration)
    {
        validate(configuration);
        return new(minimumDuration: TimeSpan.FromSeconds(int.Parse(configuration.Parameters["delaySeconds"], CultureInfo.InvariantCulture)));
    }
    public static CapabilityBinding Binding(string capabilityId, TimeSpan? timeout = null) => new(new(capabilityId, RestorableSettingInvocations.Schema, RestorableSettingInvocations.Schema,
        ScopeKind.Session, true, false, true, ExecutionLocation.SessionAgent, "same-user-session", true, "available", true,
        timeout ?? TimeSpan.FromSeconds(15), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active condition owned setting", "same user", []), ScalarKind.Text, ScalarKind.Text);
}
