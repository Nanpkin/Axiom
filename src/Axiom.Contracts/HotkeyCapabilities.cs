using System.Collections.Immutable;
using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class HotkeyCapabilities
{
    public const string Pressed = "keyboard.hotkey";
    public const string Send = "keyboard.send";
    public static ImmutableArray<string> Keys { get; } = [.. KeyboardShortcuts.Keys,
        .. Enumerable.Range(12, 13).Select(i => "F" + i), "Enter", "Tab", "Escape", "Space", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown", "Left", "Right", "Up", "Down"];
    public static TaskCapability Detection { get; } = new(Pressed, 1, TaskCapabilityKind.Detection, "键盘", "按下全局快捷键时",
        "按下任意一个选中的组合键时触发。",
        [new("chords", "触发快捷键", TaskFieldKind.HotkeyList, true, "全局登记会占用组合键；必须包含 Ctrl 或 Alt。已被其他程序占用时不会生效。", null, 1024, [])],
        "每次按下触发一次，按住不重复。窗口关闭后继续监听，暂停后释放不再需要的组合。",
        "重新连接不补发旧按键；动作忙时的新按键不排队重放。多组合中任意一个都能触发。");
    public static TaskCapability Action { get; } = new(Send, 1, TaskCapabilityKind.Action, "键盘", "模拟按下组合键",
        "按顺序向当前前台窗口发送组合键，每个组合都包含按下和松开。",
        [new("chords", "发送组合键", TaskFieldKind.HotkeyList, true, "可添加多个，按列表顺序发送。不能发送 Axiom 当前监听的组合，以免触发循环；不会自动切换前台窗口。", null, 1024, []),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "等待后向当时的前台窗口发送；暂停或删除会取消尚未发送的动作。", "0", 8, [], 0, 86400, "更多选项")],
        "结果只确认输入已发送，不保证目标软件执行其功能。失败或未知不自动重试。",
        "修改、暂停或删除不会撤销已发送按键。安全桌面或拒绝模拟输入的软件可能无法接收。");

    public static ShortcutChord[] ParseList(string? text, bool detection)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024) throw new ArgumentException("请添加组合键。");
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 1 or > 16) throw new ArgumentException("最多添加 16 个组合键。");
        var chords = lines.Select(Parse).ToArray();
        if (detection && chords.Any(c => (c.Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) == 0 || c.Key == "F12"))
            throw new ArgumentException("全局快捷键必须包含 Ctrl 或 Alt，F12 为系统保留按键。");
        return detection ? chords.Distinct().ToArray() : chords;
    }
    public static ShortcutChord Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 5 || parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("组合键格式无效。");
        var flags = ShortcutModifiers.None;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToUpperInvariant() switch { "CTRL" => ShortcutModifiers.Control, "ALT" => ShortcutModifiers.Alt,
                "SHIFT" => ShortcutModifiers.Shift, "WIN" => ShortcutModifiers.Windows, _ => throw new ArgumentException("未知修饰键：" + part) };
            if (flags.HasFlag(flag)) throw new ArgumentException("修饰键不能重复。");
            flags |= flag;
        }
        var key = Keys.FirstOrDefault(k => k.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("不支持这个按键。");
        return new(flags, key);
    }
    public static ShortcutChord[] Validate(TaskInvocation config)
    {
        if (config.CapabilityId is not (Pressed or Send)) throw new ArgumentException("键盘能力无效。");
        var descriptor = config.CapabilityId == Pressed ? Detection : Action;
        if (config.SchemaVersion != 1 || config.Parameters.Keys.Any(k => !descriptor.Fields.Any(f => f.Id == k))) throw new ArgumentException("键盘能力参数无效。");
        if (config.CapabilityId == Send) Delay(config);
        config.Parameters.TryGetValue("chords", out var text);
        return ParseList(text, config.CapabilityId == Pressed);
    }
    public static string ResourceId(IEnumerable<ShortcutChord> chords) => string.Join("\n", chords.Select(c => c.ToString()).Distinct().Order(StringComparer.Ordinal));
    public static uint Modifiers(ShortcutChord chord) => (chord.Modifiers.HasFlag(ShortcutModifiers.Control) ? 2u : 0) |
        (chord.Modifiers.HasFlag(ShortcutModifiers.Alt) ? 1u : 0) | (chord.Modifiers.HasFlag(ShortcutModifiers.Shift) ? 4u : 0) | (chord.Modifiers.HasFlag(ShortcutModifiers.Windows) ? 8u : 0);
    public static ushort VirtualKey(ShortcutChord chord) => (ushort)(chord.Key.Length == 1 ? chord.Key[0] : chord.Key.StartsWith('F') && int.TryParse(chord.Key[1..], out var f) ? 0x6F + f : chord.Key switch
    {
        "Backspace" => 0x08, "Tab" => 0x09, "Enter" => 0x0D, "Escape" => 0x1B, "Space" => 0x20, "PageUp" => 0x21, "PageDown" => 0x22,
        "End" => 0x23, "Home" => 0x24, "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28, "Insert" => 0x2D, "Delete" => 0x2E,
        _ => throw new ArgumentException("不支持这个按键。")
    });
    public static double Delay(TaskInvocation config) => config.Parameters.TryGetValue("delaySeconds", out var value) && double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 86400
        ? seconds : throw new ArgumentException("延迟执行参数无效。");
}
