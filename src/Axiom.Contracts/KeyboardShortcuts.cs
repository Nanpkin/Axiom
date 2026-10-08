using System.Collections.Immutable;

namespace Axiom.Contracts;

[Flags]
public enum ShortcutModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }
public sealed record ShortcutChord(ShortcutModifiers Modifiers, string Key)
{
    public override string ToString() => string.Join(" + ", new[] {
        Modifiers.HasFlag(ShortcutModifiers.Control) ? "Ctrl" : null,
        Modifiers.HasFlag(ShortcutModifiers.Alt) ? "Alt" : null,
        Modifiers.HasFlag(ShortcutModifiers.Shift) ? "Shift" : null,
        Modifiers.HasFlag(ShortcutModifiers.Windows) ? "Win" : null, Key }.Where(x => x is not null));
}
public sealed record ApplicationFunction(string Id, string Name, string Description);
public sealed record ShortcutBinding(string Id, long Version, string FunctionId, ShortcutChord Chord, bool Enabled);
public sealed record ShortcutState(ShortcutBinding Binding, bool Registered, string? Error = null);
public sealed record ShortcutSnapshot(ImmutableArray<ApplicationFunction> Functions, ImmutableArray<ShortcutState> Shortcuts,
    bool ListenerAvailable, string? LastError = null);

public static class KeyboardShortcuts
{
    public const string ShowWindow = "app.show-window";
    public const int MaximumBindings = 32;
    public static ImmutableArray<string> Keys { get; } = [.. Enumerable.Range('A', 26).Select(x => ((char)x).ToString()),
        .. Enumerable.Range('0', 10).Select(x => ((char)x).ToString()), .. Enumerable.Range(1, 11).Select(x => "F" + x)];
    public static string AgentPipe(string sid) => "axiom-shortcuts-agent-v1-" + sid.Replace('-', '_');
    public static void Validate(ShortcutBinding binding)
    {
        if (!Guid.TryParseExact(binding.Id, "D", out _) || binding.Version < 0 ||
            string.IsNullOrWhiteSpace(binding.FunctionId) || binding.FunctionId.Length > 80 || binding.Chord is null ||
            !Keys.Contains(binding.Chord.Key) || (binding.Chord.Modifiers & ~(ShortcutModifiers)15) != 0 ||
            (binding.Chord.Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) == 0)
            throw new ArgumentException("请选择 Ctrl 或 Alt，配上字母、数字或 F1–F11。快捷键配置无效。");
    }
    public static void ValidateSet(IEnumerable<ShortcutBinding> bindings)
    {
        var items = bindings.ToArray();
        if (items.Length > MaximumBindings || items.Select(x => x.Id).Distinct().Count() != items.Length)
            throw new ArgumentException("最多保存 32 个快捷键，且不能重复标识。");
        foreach (var item in items) Validate(item);
        if (items.Where(x => x.Enabled).Select(x => x.Chord).Distinct().Count() != items.Count(x => x.Enabled))
            throw new ArgumentException("这个组合键已分配给另一条快捷键，请选择其他组合。");
    }
}

public interface IShortcutStore
{
    ShortcutBinding[] Read();
    ShortcutBinding Save(ShortcutBinding binding);
    void Delete(string id, long version);
}
