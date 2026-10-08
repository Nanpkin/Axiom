using System.Collections.Immutable;
using Axiom.Contracts;

namespace Axiom.SessionAgent;

internal sealed class ShortcutActionRegistry
{
    private readonly Dictionary<string, (ApplicationFunction Descriptor, Action Invoke)> actions = [];
    public ImmutableArray<ApplicationFunction> Functions => [.. actions.Values.Select(x => x.Descriptor)];
    public void Register(ApplicationFunction descriptor, Action invoke)
    {
        if (!actions.TryAdd(descriptor.Id, (descriptor, invoke))) throw new ArgumentException("功能标识重复。");
    }
    public bool Contains(string id) => actions.ContainsKey(id);
    public void Invoke(string id)
    {
        if (!actions.TryGetValue(id, out var action)) throw new InvalidOperationException("这个快捷键功能暂不可用。");
        action.Invoke();
    }
}

internal interface IShortcutNative
{
    bool Register(int id, ShortcutChord chord);
    void Unregister(int id);
}

// Runs entirely on the native listener thread, including reads and dispatch.
internal sealed class ShortcutRegistrationSet(ShortcutActionRegistry actions, IShortcutNative native)
{
    private sealed record Registration(ShortcutBinding Binding, int? NativeId, string? Error);
    private Dictionary<string, Registration> current = [];
    private int nextId;
    private string? lastError;
    public ShortcutSnapshot Read() => new(actions.Functions, [.. current.Values.Select(x => new ShortcutState(x.Binding, x.NativeId.HasValue, x.Error))], true, lastError);
    public ShortcutSnapshot Replace(ShortcutBinding[] bindings, bool restore = false)
    {
        KeyboardShortcuts.ValidateSet(bindings);
        if (!restore && bindings.Any(x => x.Enabled && !actions.Contains(x.FunctionId))) throw new ArgumentException("这个快捷键功能暂不可用。");
        var staged = new Dictionary<string, Registration>(); var allocated = new List<int>();
        try
        {
            foreach (var binding in bindings)
            {
                if (!binding.Enabled) { staged.Add(binding.Id, new(binding, null, null)); continue; }
                if (!actions.Contains(binding.FunctionId)) { staged.Add(binding.Id, new(binding, null, "这个功能暂不可用，请更新 Axiom。")); continue; }
                var reusable = current.Values.FirstOrDefault(x => x.NativeId.HasValue && x.Binding.Chord == binding.Chord);
                if (reusable is not null) { staged.Add(binding.Id, reusable with { Binding = binding }); continue; }
                int id;
                do { nextId = nextId % 0xBFFF + 1; id = nextId; } while (current.Values.Any(x => x.NativeId == id) || allocated.Contains(id));
                if (!native.Register(id, binding.Chord))
                {
                    var error = $"{binding.Chord} 已被其他程序或 Windows 占用，请换一个组合。";
                    if (!restore) throw new InvalidOperationException(error);
                    staged.Add(binding.Id, new(binding, null, error)); continue;
                }
                allocated.Add(id); staged.Add(binding.Id, new(binding, id, null));
            }
        }
        catch { foreach (var id in allocated) native.Unregister(id); throw; }
        var retained = staged.Values.Where(x => x.NativeId.HasValue).Select(x => x.NativeId!.Value).ToHashSet();
        foreach (var item in current.Values) if (item.NativeId is { } id && !retained.Contains(id)) native.Unregister(id);
        current = staged; return Read();
    }
    public void Press(int nativeId)
    {
        var item = current.Values.SingleOrDefault(x => x.NativeId == nativeId);
        if (item is null) return;
        try { actions.Invoke(item.Binding.FunctionId); lastError = null; }
        catch (Exception error) { lastError = "快捷键未能完成：" + error.Message; }
    }
}
