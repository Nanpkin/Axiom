using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsPowerSaverState
{
    public static async Task ObserveAsync(string[] resources, Action<PowerContextValue[], bool> publish, CancellationToken token)
    {
        using var events = new WindowsSessionEvents(saverOnly: true); // subscribe before the first read
        await PowerSaverStateObservation.RunAsync(resources, events.Events, () => WindowsPowerState.ReadSaverStatus(token), publish, token, () => events.Failed);
    }
}
