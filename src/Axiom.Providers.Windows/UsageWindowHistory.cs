namespace Axiom.Providers.Windows;

public sealed record UsageWindowChange(string Kind, string Executable);

public sealed class UsageWindowHistory
{
    private readonly Dictionary<nint, (string Path, string State, int Pid)> windows = [];
    public UsageWindowChange? Update(nint window, string path, string state, bool baseline = false, int pid = 0)
    {
        if (state is not ("normal" or "minimized" or "maximized")) throw new ArgumentException("Invalid window state.");
        var had = windows.TryGetValue(window, out var previous);
        if (!had && windows.Count >= 4096) throw new IOException("窗口观察容量达到上限，记录不完整。");
        windows[window] = (path, state, pid);
        if (baseline) return null;
        if (!had || previous.Pid != pid || !previous.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return new("window-shown", path);
        return previous.State == state ? null : new("window-" + state, path);
    }
    public UsageWindowChange? Hide(nint window) => windows.Remove(window, out var previous) ? new("window-hidden", previous.Path) : null;
    public bool Unchanged(nint window, int pid, string state) => windows.TryGetValue(window, out var prior) && prior.Pid == pid && prior.State == state;
    public void Forget(nint window) => windows.Remove(window);
}
