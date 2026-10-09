using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public static class PowerSaverStateObservation
{
    public static Task RunAsync(string[] resources, ChannelReader<string> notifications, Func<byte> query,
        Action<PowerContextValue[], bool> publish, CancellationToken token, Func<bool>? notificationFailed = null) =>
        PowerContextObservation.RunAsync(resources, notifications, "power-saver-changed", "power-saver-reset",
            () => PowerSaverStateState.Samples(resources, query()), publish, token, notificationFailed);
}
