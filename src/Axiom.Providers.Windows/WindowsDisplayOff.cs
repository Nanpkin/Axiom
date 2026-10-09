using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsDisplayOff
{
    private static readonly BoundedNativeCommand Commands = new("关闭屏幕请求", TimeSpan.FromSeconds(5));
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> RequestAsync(string sid, CancellationToken token) =>
        Commands.RunAsync(cancellation => DisplayOffOperation.Apply(new Api(sid, cancellation), cancellation), token);

    private sealed class Api(string sid, CancellationToken token) : IDisplayOffApi
    {
        public SessionLockState GetCurrentState()
        {
            token.ThrowIfCancellationRequested();
            using var identity = WindowsIdentity.GetCurrent();
            if (string.IsNullOrWhiteSpace(sid) || identity.User?.Value != sid || !Environment.UserInteractive) return SessionLockState.NonInteractive;
            using var current = Process.GetCurrentProcess(); var sessionId = current.SessionId;
            if (sessionId <= 0) return SessionLockState.NonInteractive;
            var accepted = WTSQuerySessionInformation(0, sessionId, 25, out var allocation, out var size); // WTSInfoEx
            try
            {
                token.ThrowIfCancellationRequested();
                if (!accepted || allocation == 0 || size < 20) return SessionLockState.Unknown;
                var header = new byte[20]; Marshal.Copy(allocation, header, 0, header.Length);
                return SessionLockStateReader.Parse(header, sessionId);
            }
            finally { if (allocation != 0) WTSFreeMemory(allocation); }
        }
        public (bool Accepted, int Error) RequestDisplayOff()
        {
            token.ThrowIfCancellationRequested();
            var instance = GetModuleHandle(null); var name = "AxiomDisplayOff." + Guid.NewGuid().ToString("N");
            nint window = 0; var registered = false;
            WindowProcedure procedure = DefWindowProc;
            try
            {
                var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance, Name = name, Procedure = procedure };
                if (RegisterClassEx(ref cls) == 0) return (false, Marshal.GetLastPInvokeError());
                registered = true;
                // An owned hidden top-level window delegates the documented system command to DefWindowProc.
                // Do not broadcast: SendMessageTimeout's timeout applies separately to each recipient.
                window = CreateWindowEx(0, name, "", 0, 0, 0, 0, 0, 0, 0, instance, 0);
                if (window == 0) return (false, Marshal.GetLastPInvokeError());
                token.ThrowIfCancellationRequested();
                // WM_SYSCOMMAND, SC_MONITORPOWER, lParam=2 (off); no lock/sleep/power-plan change.
                // Same-thread messages run synchronously; the outer bounded command retains admission
                // if a driver blocks past the caller deadline, rather than spawning more native work.
                Marshal.SetLastPInvokeError(0);
                var accepted = SendMessageTimeout(window, 0x0112, 0xF170, 2, 0x0002 | 0x0020, 1000, out _) != 0;
                return (accepted, accepted ? 0 : Marshal.GetLastPInvokeError());
            }
            finally
            {
                if (window != 0) DestroyWindow(window);
                if (registered) UnregisterClass(name, instance);
                GC.KeepAlive(procedure);
            }
        }
    }
    private delegate nint WindowProcedure(nint window, uint message, nint w, nint l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Size, Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; public nint SmallIcon; }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(nint server, int session, int information, out nint allocation, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint allocation);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extra, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nint w, nint l, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
