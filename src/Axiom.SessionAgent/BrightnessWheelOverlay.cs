using System.Runtime.InteropServices;

namespace Axiom.SessionAgent;

// Owned by the existing shortcut message loop. The window and its timer live only during feedback.
internal sealed class BrightnessWheelOverlay
{
    private const uint Topmost = 8, Popup = 0x80000000, Layered = 0x80000, NoActivate = 0x08000000, ToolWindow = 0x80, Transparent = 0x20;
    private const int HoldMilliseconds = 850, FadeMilliseconds = 180, Alpha = 224;
    private nint window, font, reorderHook;
    private int low, high, dpi;
    private long lastInput;
    private readonly SubclassProc procedure;
    private readonly WinEventProc onReorder;

    internal BrightnessWheelOverlay()
    {
        procedure = WindowProc;
        onReorder = OnReorder;
    }
    private int Scale(int value) => (int)Math.Round(value * dpi / 96d);

    internal void Show(int minimum, int maximum)
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), 1); // Primary fallback during a desktop transition.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var work = info.Work;
        if (window == 0)
        {
            window = CreateWindowEx(Topmost | Layered | Transparent | NoActivate | ToolWindow, "STATIC", "Axiom 亮度",
                Popup, work.Left, work.Top, 0, 0, 0, 0, 0, 0);
            if (window == 0) return;
            if (!SetWindowSubclass(window, procedure, 1, 0)) { Close(); return; }
            const uint objectReorder = 0x8004;
            reorderHook = SetWinEventHook(objectReorder, objectReorder, 0, onReorder, 0, 0, 0);
        }
        var scale = (int)GetDpiForWindow(window);
        if (dpi != scale)
        {
            dpi = scale;
            if (font != 0) DeleteObject(font);
            font = CreateFont(-Scale(15), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var region = CreateRoundRectRgn(0, 0, Scale(214) + 1, Scale(48) + 1, Scale(24), Scale(24));
            if (region != 0 && SetWindowRgn(window, region, false) == 0) DeleteObject(region);
        }
        low = minimum;
        high = maximum;
        lastInput = Environment.TickCount64;
        SetLayeredWindowAttributes(window, 0, Alpha, 2);
        SetWindowPos(window, -1, work.Right - Scale(238), work.Bottom - Scale(72), Scale(214), Scale(48), 0x50); // TOPMOST, SHOWWINDOW, NOACTIVATE
        InvalidateRect(window, 0, false);
        if (SetTimer(window, 1, HoldMilliseconds, 0) == 0) Close();
    }

    private void OnReorder(nint hook, uint eventId, nint source, int objectId, int childId, uint thread, uint time)
    {
        // Top-level z-order changes are reported on the desktop. Ignore control-tree reordering.
        if (hook != reorderHook || window == 0 || source != GetDesktopWindow()) return;
        var foreground = GetForegroundWindow();
        if (foreground == 0) return;
        // EnumWindows visits top-level windows front to back. Reassert only if the foreground
        // actually overtook us; our own reorder notification then has nothing to do.
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == window) return false;
            if (hwnd != foreground) return true;
            SetWindowPos(window, -1, 0, 0, 0, 0, 0x13); // NOSIZE | NOMOVE | NOACTIVATE
            return false;
        }, 0);
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        switch (message)
        {
            case 0x14: return 1; // WM_ERASEBKGND
            case 0x0F: // WM_PAINT
                var dc = BeginPaint(hwnd, out var paint);
                if (dc != 0) Draw(dc);
                EndPaint(hwnd, ref paint);
                return 0;
            case 0x113 when wParam == 1: // WM_TIMER
                var fade = Environment.TickCount64 - lastInput - HoldMilliseconds;
                if (fade < 0) return 0; // Stale timer after a new wheel event.
                if (fade >= FadeMilliseconds) Close();
                else
                {
                    SetLayeredWindowAttributes(hwnd, 0, (byte)(Alpha * (FadeMilliseconds - fade) / FadeMilliseconds), 2);
                    if (SetTimer(hwnd, 1, 24, 0) == 0) Close();
                }
                return 0;
            default: return DefSubclassProc(hwnd, message, wParam, lParam);
        }
    }

    private void Draw(nint dc)
    {
        var saved = SaveDC(dc);
        SelectObject(dc, GetStockObject(18)); // DC_BRUSH
        SelectObject(dc, GetStockObject(8));  // NULL_PEN
        void Pill(int x, int y, int width, int height, int radius, uint color)
        {
            if (width <= 0) return;
            SetDCBrushColor(dc, color);
            RoundRect(dc, Scale(x), Scale(y), Scale(x + width), Scale(y + height), Scale(radius), Scale(radius));
        }
        Pill(0, 0, 215, 49, 0, 0x29251F);
        int track = low == high ? 129 : 98;
        Pill(17, 21, track, 6, 6, 0x59534C);
        if (low != high) Pill(17, 21, (track * high + 50) / 100, 6, 6, 0x94886A);
        Pill(17, 21, (track * low + 50) / 100, 6, 6, 0xC7D49A);
        if (font != 0) SelectObject(dc, font);
        SetBkMode(dc, 1);
        SetTextColor(dc, 0xFAF9F6);
        var bounds = new Rect { Left = Scale(track + 20), Right = Scale(198), Bottom = Scale(48) };
        DrawText(dc, low == high ? $"{low}%" : $"{low}–{high}%", -1, ref bounds, 0x26);
        RestoreDC(dc, saved);
    }

    internal void Close()
    {
        if (reorderHook != 0) { UnhookWinEvent(reorderHook); reorderHook = 0; }
        if (window != 0) DestroyWindow(window); // Also releases its timer and window region.
        if (font != 0) DeleteObject(font);
        window = font = 0;
        dpi = 0;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    // PAINTSTRUCT is 72 bytes on supported Windows x64.
    [StructLayout(LayoutKind.Sequential, Size = 72)] private struct Paint { private nint dc; }
    [DllImport("comctl32")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern bool DestroyWindow(nint hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate void WinEventProc(nint hook, uint eventId, nint source, int objectId, int childId, uint thread, uint time);
    private delegate bool WindowVisitor(nint hwnd, nint data);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern nint GetDesktopWindow();
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32")] private static extern bool EnumWindows(WindowVisitor visit, nint data);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32")] private static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32")] private static extern nint BeginPaint(nint hwnd, out Paint paint);
    [DllImport("user32")] private static extern bool EndPaint(nint hwnd, ref Paint paint);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int length, ref Rect rect, uint flags);
    [DllImport("gdi32")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int h, int w, int angle, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] private static extern nint GetStockObject(int id);
    [DllImport("gdi32")] private static extern uint SetDCBrushColor(nint dc, uint color);
    [DllImport("gdi32")] private static extern bool RoundRect(nint dc, int left, int top, int right, int bottom, int w, int h);
    [DllImport("gdi32")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32")] private static extern int SaveDC(nint dc);
    [DllImport("gdi32")] private static extern bool RestoreDC(nint dc, int saved);
}
