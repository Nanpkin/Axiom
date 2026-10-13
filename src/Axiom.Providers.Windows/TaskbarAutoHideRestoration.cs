namespace Axiom.Providers.Windows;

// One actual shell and one first baseline. Only AUTOHIDE is owned; other flags remain the current shell's flags.
internal sealed class TaskbarAutoHideRestoration
{
    private readonly ITaskbarAutoHideApi api;
    private readonly ConditionalSettingTransaction<bool> setting;
    private uint lastRead;
    internal TaskbarAutoHideRestoration(ITaskbarAutoHideApi api)
    {
        this.api = api;
        var baseline = Read();
        setting = new(baseline, Read, Write, "任务栏自动隐藏");
    }
    internal RestorableSettingResult Apply(bool enabled, CancellationToken token) => setting.Apply(enabled, token);
    internal RestorableSettingResult Release(CancellationToken token) => setting.Release(token);
    internal void Relinquish() => setting.Relinquish();
    private bool Read()
    {
        if (!api.IsCurrent) throw new IOException("本次系统Shell身份无法核验。");
        var state = api.ReadState();
        if (!state.HasValue || !api.IsCurrent) throw new IOException("本次任务栏状态未知。");
        lastRead = state.Value;
        return (state.Value & TaskbarAutoHideOperation.AutoHide) != 0;
    }
    private void Write(bool enabled)
    {
        // Recheck before the one write; keep unrelated flags from the current read, not the first baseline.
        var state = api.ReadState();
        if (!state.HasValue || !api.IsCurrent || ((state.Value ^ lastRead) & TaskbarAutoHideOperation.AutoHide) != 0)
            throw new IOException("任务栏设置或Shell已变化，本次让出。");
        var target = enabled ? state.Value | TaskbarAutoHideOperation.AutoHide : state.Value & ~TaskbarAutoHideOperation.AutoHide;
        if (!api.SetState(target)) throw new IOException("任务栏请求未确认，不重试。");
    }
}
