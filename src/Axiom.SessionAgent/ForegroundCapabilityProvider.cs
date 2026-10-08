using System.Diagnostics;
using System.Security.Principal;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class ForegroundCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [ForegroundCapabilities.Active];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Any(r => r.CapabilityId != ForegroundCapabilities.Active || r.ResourceId.Length is < 5 or > 132 ||
            !r.ResourceId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || r.ResourceId.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)) throw new ArgumentException("前台检测参数无效。");
        using var process = Process.GetCurrentProcess(); using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        using var source = new WindowsForegroundEvents(); var first = true;
        await foreach (var _ in source.Events.ReadAllAsync(token))
        {
            var path = WindowsForegroundIdentity.Resolve(WindowsForegroundEvents.ReadCurrent(), sid, process.SessionId);
            var samples = ForegroundCapabilities.Samples(resources, path is null ? null : Path.GetFileName(path));
            publish(new(first, samples)); first = false;
        }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("前台应用只能作为检测。");
}
