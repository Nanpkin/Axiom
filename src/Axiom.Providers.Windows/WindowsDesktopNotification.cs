using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

internal interface IDesktopNotificationShell
{
    bool Show(nint window, string title, string body, bool sound);
    void Remove(nint window);
}
[SupportedOSPlatform("windows")]
public static class WindowsDesktopNotification
{
    private static readonly BoundedNativeCommand Commands = new("系统提醒", TimeSpan.FromSeconds(15));
    public static Task<(bool Success, string Reason)> ShowAsync(string title, string body, bool sound, CancellationToken token) => ShowAsync(title, body, sound, token, new NativeShell());
    internal static Task<(bool Success, string Reason)> ShowAsync(string title, string body, bool sound, CancellationToken token, IDesktopNotificationShell shell)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 63 || title.Any(char.IsControl) || string.IsNullOrWhiteSpace(body) || body.Length > 255 ||
            body.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new ArgumentException("提醒文字无效。");
        return Commands.RunAsync(cancellation => Run(title, body, sound, shell, cancellation), token);
    }
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    private static (bool Success, string Reason) Run(string title, string body, bool sound, IDesktopNotificationShell shell, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var instance = GetModuleHandle(null); var name = "AxiomReminder." + Guid.NewGuid().ToString("N");
        nint window = 0; nuint timer = 0; var registered = false; var added = false;
        WindowProcedure procedure = (handle, message, w, l) =>
        {
            if (message == 0x10 || message == 0x113 || message == 0x8001 && ((long)l & 0xFFFF) is 0x403 or 0x404 or 0x405)
            { PostQuitMessage(0); return 0; }
            return DefWindowProc(handle, message, w, l);
        };
        try
        {
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance, Name = name, Procedure = procedure };
            if (RegisterClassEx(ref cls) == 0) throw new IOException("无法创建提醒消息类。"); registered = true;
            window = CreateWindowEx(0, name, "", 0, 0, 0, 0, 0, new nint(-3), 0, instance, 0);
            if (window == 0) throw new IOException("无法创建提醒消息窗口。");
            using var canceled = token.Register(() => PostMessage(window, 0x10, 0, 0));
            token.ThrowIfCancellationRequested();
            timer = SetTimer(window, 1, 10000, 0);
            if (timer == 0) throw new IOException("无法设置提醒清理期限。");
            added = shell.Show(window, title, body, sound);
            if (!added) return (false, "Windows未接受提醒请求；通知区域或当前交互桌面可能不可用。");
            int received;
            while ((received = GetMessage(out var message, 0, 0, 0)) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            if (received < 0) throw new IOException("提醒消息处理未确认结束。");
            token.ThrowIfCancellationRequested();
            return (true, "Windows已接受提醒请求；显示受系统勿扰和通知设置影响，不代表已阅读。");
        }
        finally
        {
            if (timer != 0) KillTimer(window, timer);
            try { if (added) shell.Remove(window); }
            finally
            {
                if (window != 0) DestroyWindow(window);
                if (registered) UnregisterClass(name, instance);
                GC.KeepAlive(procedure);
            }
        }
    }
    private sealed class NativeShell : IDesktopNotificationShell
    {
        public bool Show(nint window, string title, string body, bool sound)
        {
            var data = Data(window); data.Flags = 1 | 2 | 4 | 16 | 64; // MESSAGE, ICON, TIP, INFO, REALTIME: don't queue a delayed reminder
            data.Message = 0x8001; data.Icon = LoadIcon(0, new nint(32516)); data.Tip = "Axiom";
            data.Title = title; data.Info = body; data.InfoFlags = sound ? 1u : 1u | 16u;
            return Shell_NotifyIcon(0, ref data);
        }
        public void Remove(nint window) { var data = Data(window); Shell_NotifyIcon(2, ref data); }
        private static IconData Data(nint window) => new() { Size = (uint)Marshal.SizeOf<IconData>(), Window = window, Id = 1, Tip = "", Info = "", Title = "" };
    }
    private delegate nint WindowProcedure(nint window, uint message, nint w, nint l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Size, Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct IconData
    {
        public uint Size; public nint Window; public uint Id, Flags, Message; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, Mask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint Balloon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extra, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint w, nint l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint operation, ref IconData data);
}
