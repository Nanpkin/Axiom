using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

/// <summary>
/// Event-driven hints for processes that create or show a window in this desktop session.
/// A hint is not a process-start event; consumers must verify process identity and lifetime.
/// </summary>
public sealed class WindowsWindowProcessHints : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint OutOfContext = 0;
    private const uint SkipOwnProcess = 2;
    private const uint WmQuit = 0x0012;

    private readonly Channel<uint> pids = Channel.CreateBounded<uint>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WinEventCallback callback;
    private uint threadId;
    private int gap;
    private int disposed;

    public WindowsWindowProcessHints()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        callback = OnEvent;
        thread = new Thread(Run) { IsBackground = true, Name = "Axiom window-process hints" };
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    public ChannelReader<uint> ProcessIds => pids.Reader;

    /// <summary>True means at least one hint may have been lost and a fresh snapshot is required.</summary>
    public bool TakeGap() => Interlocked.Exchange(ref gap, 0) != 0;

    private void Run()
    {
        threadId = GetCurrentThreadId();
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Ensure PostThreadMessage has a queue.
        var hook = SetWinEventHook(EventObjectCreate, EventObjectShow, IntPtr.Zero,
            callback, 0, 0, OutOfContext | SkipOwnProcess);
        if (hook == IntPtr.Zero)
        {
            ready.TrySetException(new InvalidOperationException($"SetWinEventHook failed: {Marshal.GetLastWin32Error()}"));
            pids.Writer.TryComplete();
            return;
        }

        ready.TrySetResult();
        try { while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } }
        finally
        {
            UnhookWinEvent(hook);
            pids.Writer.TryComplete();
        }
    }

    private void OnEvent(IntPtr _, uint eventType, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (Volatile.Read(ref disposed) != 0 || window == IntPtr.Zero || objectId != 0 || childId != 0)
            return;
        if (GetAncestor(window, 2) != window) return; // GA_ROOT: ignore child controls.
        if (GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0) return;
        if (!pids.Writer.TryWrite(pid)) Interlocked.Exchange(ref gap, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (threadId != 0) PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(5));
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
