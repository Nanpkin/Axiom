using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

/// <summary>A blocking message loop; keyboard input is delivered by RegisterHotKey only.</summary>
public sealed class WindowsGlobalHotkeys : IDisposable
{
    private const uint WorkMessage = 0x8001, HotkeyMessage = 0x0312, QuitMessage = 0x0012;
    private readonly Channel<Action> work = Channel.CreateBounded<Action>(64);
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> ids = [];
    private readonly object gate = new();
    private uint threadId;
    private bool disposed;
    public event Action<int>? Pressed;
    public WindowsGlobalHotkeys()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        thread = new Thread(Run) { IsBackground = true, Name = "Axiom global shortcuts" }; thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }
    public Task<T> ExecuteAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!work.Writer.TryWrite(() => { try { completion.SetResult(action()); } catch (Exception error) { completion.SetException(error); } }))
                throw new InvalidOperationException("快捷键后台正忙，请稍后重试。");
            if (!PostThreadMessage(threadId, WorkMessage, 0, 0)) throw new InvalidOperationException("快捷键监听暂不可用。");
        }
        return completion.Task;
    }
    // These methods are intentionally valid only inside ExecuteAsync/on the message thread.
    public bool Register(int id, uint modifiers, uint key)
    {
        if (Thread.CurrentThread != thread) throw new InvalidOperationException("Hotkeys must be registered on their message thread.");
        if (!RegisterHotKey(0, id, modifiers | 0x4000, key)) return false; // MOD_NOREPEAT
        ids.Add(id); return true;
    }
    public void Unregister(int id)
    {
        if (Thread.CurrentThread != thread) throw new InvalidOperationException("Hotkeys must be unregistered on their message thread.");
        if (ids.Remove(id)) UnregisterHotKey(0, id);
    }
    private void Run()
    {
        threadId = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0); ready.TrySetResult();
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == WorkMessage) while (work.Reader.TryRead(out var action)) action();
                else if (message.Id == HotkeyMessage) Pressed?.Invoke((int)message.WParam);
            }
        }
        finally
        {
            // Accepted work must finish before the owner disposes its state.
            while (work.Reader.TryRead(out var action)) action();
            foreach (var id in ids) UnregisterHotKey(0, id); ids.Clear();
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; work.Writer.TryComplete(); PostThreadMessage(threadId, QuitMessage, 0, 0); }
        if (Thread.CurrentThread != thread) thread.Join();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Window; public uint Id; public nint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint message, nint wparam, nint lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
