using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

public sealed record KeyboardStroke(ushort Key, bool Up);
public static class WindowsKeyboardInput
{
    public static ushort[] HeldModifiers() => new ushort[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }
        .Where(k => (GetAsyncKeyState(k) & 0x8000) != 0).ToArray();
    public static bool IsHeld(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public static (bool Success, string Reason) Send(IReadOnlyList<KeyboardStroke> strokes, IReadOnlyList<ushort>? originallyHeld = null)
    {
        if (strokes.Count is < 1 or > 256) throw new ArgumentException("按键数量无效。");
        if (GetForegroundWindow() == 0) return (false, "当前没有可接收输入的前台窗口。");
        var inputs = strokes.Select(s => new Input { Type = 1, Data = new() { Keyboard = new() { Key = s.Key,
            Flags = Flags(s.Key, s.Up) } } }).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        var error = Marshal.GetLastWin32Error();
        if (sent == inputs.Length) return (true, "组合键输入已发送到当前前台窗口；是否执行由目标软件决定。");
        if (sent > 0)
        {
            // Compensate only keys whose down event actually entered the stream; never retry the command.
            var held = new HashSet<ushort>();
            foreach (var stroke in strokes.Take((int)sent)) { if (stroke.Up) held.Remove(stroke.Key); else held.Add(stroke.Key); }
            var cleanup = held.Select(k => new Input { Type = 1, Data = new() { Keyboard = new() { Key = k, Flags = Flags(k, true) } } })
                .Concat((originallyHeld ?? []).Select(k => new Input { Type = 1, Data = new() { Keyboard = new() { Key = k, Flags = Flags(k, false) } } })).ToArray();
            if (cleanup.Length > 0) SendInput((uint)cleanup.Length, cleanup, Marshal.SizeOf<Input>());
        }
        return (false, $"输入未完整发送（{sent}/{inputs.Length}，Win32 {error}）；不重试，目标软件可能拒绝模拟输入。");
    }
    private static uint Flags(ushort key, bool up) => (up ? 2u : 0) |
        (key is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5 ? 1u : 0);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
