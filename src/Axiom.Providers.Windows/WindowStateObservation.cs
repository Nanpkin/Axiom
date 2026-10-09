using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public enum WindowVisualState { Normal, Minimized, Maximized }
public sealed record WindowStateReading(bool Candidate, string? Executable, bool IdentityKnown, WindowVisualState? State);
public sealed record WindowStateValue(string Executable, WindowVisualState State, bool? Matches, bool Baseline);

// Synthetic implementations exercise notification ordering without touching the desktop.
public interface IWindowStateApi : IDisposable
{
    ChannelReader<nint> Events { get; }
    void Enumerate(Action<nint> visit);
    WindowStateReading Read(nint window);
}

public sealed class WindowStateProjection
{
    private readonly string[] paths;
    private readonly Dictionary<nint, WindowStateReading> windows = [];
    private readonly Dictionary<(string, WindowVisualState), bool?> previous = [];
    public WindowStateProjection(string[] paths) => this.paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public void Set(nint window, WindowStateReading reading)
    {
        if (!reading.Candidate || reading.Executable is not null && !paths.Contains(reading.Executable, StringComparer.OrdinalIgnoreCase))
        { windows.Remove(window); return; }
        if (!windows.ContainsKey(window) && windows.Count >= 4096) throw new IOException("窗口状态检测容量已满，状态未知。");
        windows[window] = reading;
    }

    public WindowStateValue[] Changes(bool initial = false)
    {
        List<WindowStateValue> changes = [];
        foreach (var path in paths)
        foreach (var state in Enum.GetValues<WindowVisualState>())
        {
            var found = windows.Values.Any(w => w.IdentityKnown && w.State == state && string.Equals(w.Executable, path, StringComparison.OrdinalIgnoreCase));
            var unknown = !found && windows.Values.Any(w => (!w.IdentityKnown || !w.State.HasValue) &&
                (w.Executable is null || string.Equals(w.Executable, path, StringComparison.OrdinalIgnoreCase)) &&
                (!w.State.HasValue || w.State == state));
            bool? value = unknown ? null : found;
            var key = (path, state);
            var existed = previous.TryGetValue(key, out var before);
            previous[key] = value;
            if (initial || !existed || before != value)
                changes.Add(new(path, state, value, initial || !existed || before is null && value.HasValue));
        }
        // Publish positive evidence before negatives when an HWND moves between identities.
        return [.. changes.OrderByDescending(s => s.Baseline).ThenByDescending(s => s.Matches == true)];
    }
}

public static class WindowStateObservation
{
    public static async Task ObserveAsync(IWindowStateApi api, string[] paths, Action<WindowStateValue[]> publish, CancellationToken token)
    {
        var state = new WindowStateProjection(paths);
        token.ThrowIfCancellationRequested();
        // The API has already subscribed; notifications racing this one enumeration remain queued.
        api.Enumerate(window => { token.ThrowIfCancellationRequested(); state.Set(window, api.Read(window)); });
        token.ThrowIfCancellationRequested();
        publish(state.Changes(initial: true));
        await foreach (var window in api.Events.ReadAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            // Coalesce already queued hints without a timer; each HWND is re-read at most once per batch.
            var pending = new HashSet<nint> { window };
            for (var i = 0; i < 255 && api.Events.TryRead(out var queued); i++) pending.Add(queued);
            foreach (var target in pending) { token.ThrowIfCancellationRequested(); state.Set(target, api.Read(target)); }
            var changes = state.Changes();
            if (changes.Length != 0) publish(changes);
        }
        if (!token.IsCancellationRequested) throw new IOException("窗口状态通知已结束，检测状态未知。");
    }
}
