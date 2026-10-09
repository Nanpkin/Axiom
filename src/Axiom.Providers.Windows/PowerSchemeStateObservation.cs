using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public static class PowerSchemeStateObservation
{
    public static Task RunAsync(string[] resources, ChannelReader<string> notifications, Func<Guid> query,
        Action<PowerContextValue[], bool> publish, CancellationToken token, Func<bool>? notificationFailed = null) =>
        PowerContextObservation.RunAsync(resources, notifications, "power-scheme-changed", "power-scheme-reset",
            () => PowerSchemeStateState.Samples(resources, query()), publish, token, notificationFailed);
}
