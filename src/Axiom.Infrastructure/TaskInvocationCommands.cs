using System.Text.Json;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

// Shared recipe encoding for session command plugins. The engine still receives a closed typed value.
public static class TaskInvocationCommands
{
    public static ValueSchema Schema { get; } = new("axiom.task-invocation", 1);
    public static RuleAction Compile(TaskInvocation configuration, TaskCapability descriptor, ResourceScope scope, string resourceId)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, descriptor);
        return RuleAction.Command(new(descriptor.Id, resourceId, scope), StateValue.From(Schema, JsonSerializer.Serialize(configuration)));
    }
}
