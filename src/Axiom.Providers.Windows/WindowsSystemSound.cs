using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsSystemSound
{
    private static readonly BoundedNativeCommand Commands = new("系统提示音", TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> RequestAsync(string sound, CancellationToken token)
    {
        SystemSoundOperation.Type(sound);
        return Commands.RunAsync(cancellation => SystemSoundOperation.Apply(sound, new Api(), cancellation), token);
    }
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    private sealed class Api : ISystemSoundApi { public bool Request(uint soundType) => MessageBeep(soundType); }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MessageBeep(uint type);
}
