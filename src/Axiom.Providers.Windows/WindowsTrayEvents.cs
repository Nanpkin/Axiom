using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Win32;

namespace Axiom.Providers.Windows;

// Shared only by pending commands and enabled continuous-tool subscriptions. No timer or global desktop hook.
[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayEvents : IDisposable
{
    private static readonly object Gate = new();
    private static Hub? shared;
    internal readonly Channel<bool> Events = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private bool disposed;
    internal WindowsTrayEvents()
    {
        lock (Gate)
        {
            shared ??= new Hub();
            try { shared.Ready.Task.GetAwaiter().GetResult(); }
            catch { var failed = shared; shared = null; if (failed.Stopped.Task.Wait(TimeSpan.FromSeconds(5))) failed.Stop.Dispose(); throw; }
            shared.Clients.Add(this);
        }
    }
    internal static void Invalidate() { lock (Gate) shared?.Publish(); }
    public void Dispose()
    {
        Hub? last;
        lock (Gate)
        {
            if (disposed) return; disposed = true; Events.Writer.TryComplete();
            shared!.Clients.Remove(this); if (shared.Clients.Count != 0) return;
            last = shared; shared = null; last.Stop.Set();
        }
        if (last.Stopped.Task.Wait(TimeSpan.FromSeconds(5))) last.Stop.Dispose();
    }
    private sealed class Hub
    {
        internal readonly HashSet<WindowsTrayEvents> Clients = [];
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEvent Stop = new(false);
        private readonly Callback callback;
        private readonly WindowProcedure procedure;
        private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        private nint hook, shell;
        internal Hub() { callback = OnEvent; procedure = OnMessage; new Thread(Run) { IsBackground = true, Name = "Axiom tray notifications" }.Start(); }
        internal void Publish(Exception? error = null)
        {
            lock (Gate) foreach (var client in Clients)
                if (error is null) client.Events.Writer.TryWrite(true); else client.Events.Writer.TryComplete(error);
        }
        private void BindShell()
        {
            if (hook != 0) { UnhookWinEvent(hook); hook = 0; }
            shell = FindWindow("Shell_TrayWnd", null);
            if (shell == 0) return;
            GetWindowThreadProcessId(shell, out var pid);
            if (pid == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            hook = SetWinEventHook(0x8000, 0x800c, 0, callback, pid, 0, 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        private nint OnMessage(nint window, uint message, nint wparam, nint lparam)
        {
            if (message == taskbarCreated)
            {
                try { BindShell(); Publish(); } catch (Exception error) { Publish(error); }
            }
            return DefWindowProc(window, message, wparam, lparam);
        }
        private void Run()
        {
            nint window = 0; ushort atom = 0;
            var className = "Axiom.TrayNotifications." + Guid.NewGuid().ToString("N");
            var module = GetModuleHandle(null);
            try
            {
                // Watch the stable parent: deleting/recreating NotifyIconSettings cannot orphan this registration.
                using var registry = Registry.CurrentUser.OpenSubKey("Control Panel") ?? throw new IOException("系统托盘记录不可用，无法订阅图标通知。");
                using var changed = new AutoResetEvent(false);
                void Arm()
                {
                    var error = RegNotifyChangeKeyValue(registry.Handle.DangerousGetHandle(), true, 5, changed.SafeWaitHandle.DangerousGetHandle(), true);
                    if (error != 0) throw new Win32Exception(error);
                }
                var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Instance = module, Name = className };
                atom = RegisterClassEx(ref definition);
                if (atom == 0 || taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Broadcast TaskbarCreated does not reach message-only windows; use a hidden top-level window.
                window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, 0, 0, module, 0);
                if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                // The Agent can be elevated while Explorer is not. Permit only this registered shell broadcast through UIPI.
                if (!ChangeWindowMessageFilterEx(window, taskbarCreated, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                BindShell(); Arm();
                var handles = new[] { Stop.SafeWaitHandle.DangerousGetHandle(), changed.SafeWaitHandle.DangerousGetHandle() };
                Ready.TrySetResult();
                while (true)
                {
                    var signaled = MsgWaitForMultipleObjectsEx(2, handles, uint.MaxValue, 0x04ff, 4);
                    if (signaled == 0) break;
                    if (signaled == 1) { Arm(); Publish(); }
                    else if (signaled == 2) while (PeekMessage(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
                    else throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception error) { if (!Ready.TrySetException(error)) Publish(error); }
            finally
            {
                if (hook != 0) UnhookWinEvent(hook);
                if (window != 0) DestroyWindow(window);
                if (atom != 0) UnregisterClass(className, module);
                Stopped.TrySetResult();
            }
        }
        private void OnEvent(nint eventHook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
        {
            if (window == 0 || kind is not (0x8000 or 0x8001 or 0x8002 or 0x8004 or 0x800c)) return;
            var root = GetAncestor(window, 2);
            var overflow = FindWindow("NotifyIconOverflowWindow", null);
            if (shell != 0 && (window == shell || root == shell) || overflow != 0 && (window == overflow || root == overflow)) Publish();
        }
    }
    private delegate void Callback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    private delegate nint WindowProcedure(nint window, uint message, nint wparam, nint lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        public string? Menu, Name; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("advapi32.dll")] private static extern int RegNotifyChangeKeyValue(nint key, [MarshalAs(UnmanagedType.Bool)] bool subtree, uint filter, nint signal, [MarshalAs(UnmanagedType.Bool)] bool async);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, Callback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint flags);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint timeout, uint wakeMask, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint change);
}
