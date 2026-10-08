using System.Collections.Immutable;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal sealed class GameHdrTaskBackend(GameHdrRuntime runtime, string principal, ResourceScope scope) : IAutomationTaskBackend
{
    public ImmutableArray<TaskCombination> Combinations => [new("process.running", "display.hdr")];
    public async Task<AutomationSnapshot> ReadAsync(CancellationToken token)
    {
        var status = await runtime.StatusAsync(token);
        var catalog = runtime.TaskCompiler.Catalog.Select(capability => capability.Id == "display.hdr"
            ? capability with { Fields = [.. capability.Fields.Select(field => field.Id == "display"
                ? field with { Choices = [.. status.Displays.Where(d => d.Supported == true).Select(d => new TaskChoice(d.Path, d.Name))] } : field)] }
            : capability).ToImmutableArray();
        var state = GameHdrTaskPresentation.ResultTitle(status.State);
        var reason = GameHdrTaskPresentation.ResultReason(status.State, status.Reason);
        return new([.. status.Rules.Select(GameHdrTasks.FromLegacy)], catalog, Combinations, status.AgentConnected, state, reason, status.AtUtc,
            [.. status.Displays.Select(d => new TaskObservation(d.Name, GameHdrTaskPresentation.DisplayState(d.Enabled, d.Active, status.AgentConnected)))]);
    }
    public async Task<AutomationTask> SaveAsync(AutomationTask task, CancellationToken token)
    {
        _ = runtime.TaskCompiler.Compile(task, principal, scope);
        return GameHdrTasks.FromLegacy(await runtime.SaveAsync(GameHdrTasks.ToLegacy(task), token));
    }
    public Task DeleteAsync(string id, long expectedVersion, CancellationToken token) => runtime.DeleteAsync(id, expectedVersion, token);
}
