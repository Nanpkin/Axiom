using Axiom.Contracts;

namespace Axiom.SessionAgent;

// Owned exclusively by the existing message thread; shares one native registration per chord.
internal sealed class HotkeyRegistrationHub(IShortcutNative native)
{
    private sealed record Entry(int NativeId, Dictionary<string, Action> Subscribers);
    private readonly Dictionary<ShortcutChord, Entry> entries = [];
    private int nextId;
    public bool Contains(ShortcutChord chord) => entries.ContainsKey(chord);
    public bool Register(string owner, ShortcutChord chord, Action pressed)
    {
        if (entries.Values.Any(e => e.Subscribers.ContainsKey(owner))) throw new InvalidOperationException("Duplicate hotkey owner.");
        if (!entries.TryGetValue(chord, out var entry))
        {
            int id; do { nextId = nextId % 0xBFFF + 1; id = nextId; } while (entries.Values.Any(e => e.NativeId == id));
            if (!native.Register(id, chord)) return false;
            entries.Add(chord, entry = new(id, []));
        }
        entry.Subscribers.Add(owner, pressed); return true;
    }
    public void Release(string owner)
    {
        foreach (var pair in entries.ToArray())
        {
            if (!pair.Value.Subscribers.Remove(owner)) continue;
            if (pair.Value.Subscribers.Count == 0) { native.Unregister(pair.Value.NativeId); entries.Remove(pair.Key); }
            return;
        }
    }
    public void Press(int nativeId)
    {
        var entry = entries.Values.SingleOrDefault(e => e.NativeId == nativeId);
        if (entry is null) return;
        foreach (var callback in entry.Subscribers.Values.ToArray()) callback();
    }
}
