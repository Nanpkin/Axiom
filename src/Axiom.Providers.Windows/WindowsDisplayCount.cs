using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsDisplayCount
{
    public static Task ObserveAsync(string[] resources, Action<DisplayCountValue[], bool> publish, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        return ObserveCoreAsync(resources, publish, token);
    }
    private static async Task ObserveCoreAsync(string[] resources, Action<DisplayCountValue[], bool> publish, CancellationToken token)
    {
        using var events = new WindowsSessionEvents(displayOnly: true);
        await DisplayCountObservation.RunAsync(resources, events.Events, Read, publish, token, () => events.Failed);
    }
    public static int? Read()
    {
        var count = 0; var exceeded = false;
        var complete = EnumDisplayMonitors(0, 0, (_, _, _, _) =>
        {
            if (++count > DisplayCountState.MaximumMonitors) { exceeded = true; return false; }
            return true; // Only count HMONITOR callbacks; no handle, ID, coordinates, or device properties are retained.
        }, 0);
        return DisplayCountState.ConfirmCount(count, complete && !exceeded);
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rectangle, nint data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
}
