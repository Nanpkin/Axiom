using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsPowerSchemeState
{
    public static async Task ObserveAsync(string[] resources, Action<PowerContextValue[], bool> publish, CancellationToken token)
    {
        using var events = new WindowsSessionEvents(schemeOnly: true); // subscribe before the first read
        await PowerSchemeStateObservation.RunAsync(resources, events.Events, () => WindowsPowerScheme.ReadActive(token), publish, token, () => events.Failed);
    }
}
