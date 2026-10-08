namespace Axiom.Providers.Windows;

public sealed record WindowPresenceValue(string ProcessName, bool? Visible);

// Bounded projection of native HWND hints. null name means identity could not be resolved.
public sealed class WindowPresenceState
{
    private readonly string[] names;
    private readonly Dictionary<nint, (string? Name, bool Known)> windows = [];
    public WindowPresenceState(string[] names) => this.names = names;
    public void Set(nint window, bool visible, string? name, bool known)
    {
        if (!visible || name is not null && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) { windows.Remove(window); return; }
        if (!windows.ContainsKey(window) && windows.Count >= 4096) throw new IOException("窗口检测容量已满，状态未知。");
        windows[window] = (name, known);
    }
    public WindowPresenceValue[] Samples() => [.. names.Select(name =>
    {
        var found = windows.Values.Any(w => w.Known && string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        var unknown = !found && windows.Values.Any(w => !w.Known && (w.Name is null || string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)));
        return new WindowPresenceValue(name, unknown ? null : found);
    }).OrderByDescending(s => s.Visible == true)];
}
