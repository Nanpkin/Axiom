using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class KeyboardCapabilityProvider(ShortcutAgent shortcuts) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [HotkeyCapabilities.Pressed, HotkeyCapabilities.Send];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Any(r => r.CapabilityId != HotkeyCapabilities.Pressed)) throw new ArgumentException("键盘检测资源无效。");
        var owners = new List<string>();
        try
        {
            await shortcuts.OnMessageThreadAsync(() =>
            {
                var samples = new List<ProviderSample>();
                foreach (var resource in resources)
                {
                    var chords = HotkeyCapabilities.ParseList(resource.ResourceId, true);
                    decimal counter = 0; var allocated = new List<string>(); var success = true;
                    foreach (var chord in chords)
                    {
                        var owner = "rule:" + Guid.NewGuid().ToString("N");
                        if (!shortcuts.Hotkeys.Register(owner, chord, () => publish(new(false,
                            [new(resource, StateQuality.Known, "axiom.event-count", 1, Number: ++counter, Reason: chord.ToString())])))) { success = false; break; }
                        allocated.Add(owner);
                    }
                    if (!success) foreach (var owner in allocated) shortcuts.Hotkeys.Release(owner);
                    else owners.AddRange(allocated);
                    samples.Add(new(resource, success ? StateQuality.Known : StateQuality.Unknown, "axiom.event-count", 1,
                        Number: success ? 0 : null, Reason: success ? null : "快捷键已被其他程序或 Windows 占用；请更换组合，或暂停后重新启用重试。"));
                }
                publish(new(true, [.. samples])); return true;
            });
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        finally { await shortcuts.OnMessageThreadAsync(() => { foreach (var owner in owners) shortcuts.Hotkeys.Release(owner); return true; }); }
    }
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation config, CancellationToken token)
    {
        if (config.CapabilityId != HotkeyCapabilities.Send) throw new ArgumentException("此检测能力不能作为动作执行。");
        var chords = HotkeyCapabilities.Validate(config);
        return await shortcuts.OnMessageThreadAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (chords.Any(shortcuts.Hotkeys.Contains)) return (OutcomeStatus.Rejected, "发送组合键中包含 Axiom 当前监听的快捷键；请更换组合，避免触发循环。");
            if (chords.Any(c => WindowsKeyboardInput.IsHeld(HotkeyCapabilities.VirtualKey(c))))
                return (OutcomeStatus.Rejected, "要发送的按键当前正被按住，请松开后重新触发。");
            var held = WindowsKeyboardInput.HeldModifiers(); var strokes = new List<KeyboardStroke>();
            strokes.AddRange(held.Select(k => new KeyboardStroke(k, true)));
            foreach (var chord in chords)
            {
                ushort[] modifiers = [.. new (ShortcutModifiers Flag, ushort Key)[] { (ShortcutModifiers.Control, 0xA2), (ShortcutModifiers.Alt, 0xA4),
                    (ShortcutModifiers.Shift, 0xA0), (ShortcutModifiers.Windows, 0x5B) }.Where(m => chord.Modifiers.HasFlag(m.Flag)).Select(m => m.Key)];
                strokes.AddRange(modifiers.Select(k => new KeyboardStroke(k, false)));
                strokes.Add(new(HotkeyCapabilities.VirtualKey(chord), false)); strokes.Add(new(HotkeyCapabilities.VirtualKey(chord), true));
                strokes.AddRange(modifiers.Reverse().Select(k => new KeyboardStroke(k, true)));
            }
            strokes.AddRange(held.Select(k => new KeyboardStroke(k, false)));
            var result = WindowsKeyboardInput.Send(strokes, held);
            return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
        });
    }
}
