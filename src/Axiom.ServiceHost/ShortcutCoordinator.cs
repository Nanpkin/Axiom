using Axiom.Contracts;

namespace Axiom.ServiceHost;

internal interface IShortcutAgentClient
{
    Task<ShortcutSnapshot> RequestAsync(string operation, ShortcutBinding[]? bindings, CancellationToken token);
}

internal sealed class ShortcutCoordinator(IShortcutStore store, IShortcutAgentClient agent)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<ShortcutSnapshot> HandleAsync(AutomationMessage request, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var previous = store.Read();
            if (request.Type == "restore-shortcuts") return await agent.RequestAsync("restore-shortcuts", previous, token);
            if (request.Type == "read-shortcuts")
            {
                try
                {
                    var live = await agent.RequestAsync("read-shortcuts", null, token);
                    return live with { Shortcuts = [.. previous.Select(binding => live.Shortcuts.FirstOrDefault(x => x.Binding == binding) ??
                        new(binding, false, binding.Enabled ? "尚未注册，后台重新连接后再刷新。" : null))] };
                }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
                { return new([], [.. previous.Select(x => new ShortcutState(x, false, x.Enabled ? "快捷键监听暂不可用，请重新打开 Axiom。" : null))], false); }
            }
            ShortcutBinding[] proposed;
            if (request.Type == "save-shortcut")
            {
                var binding = request.Shortcut!; KeyboardShortcuts.Validate(binding);
                var existing = previous.SingleOrDefault(x => x.Id == binding.Id);
                if ((existing?.Version ?? 0) != binding.Version) throw new InvalidOperationException("快捷键已在别处更改，请刷新后重新编辑。");
                proposed = [.. previous.Where(x => x.Id != binding.Id), binding with { Version = checked(binding.Version + 1) }];
            }
            else
            {
                if (!previous.Any(x => x.Id == request.Id && x.Version == request.ExpectedVersion))
                    throw new InvalidOperationException("快捷键已在别处更改，请刷新后重新编辑。");
                proposed = [.. previous.Where(x => x.Id != request.Id)];
            }
            KeyboardShortcuts.ValidateSet(proposed);
            try
            {
                var applied = await agent.RequestAsync("apply-shortcuts", proposed, token);
                token.ThrowIfCancellationRequested();
                if (request.Type == "save-shortcut") store.Save(request.Shortcut!);
                else store.Delete(request.Id!, request.ExpectedVersion!.Value);
                return applied;
            }
            catch
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await agent.RequestAsync("restore-shortcuts", previous, rollback.Token); }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException)
                { throw new IOException("保存结果未确认，已保存配置未更新。请重新打开 Axiom 并刷新快捷键状态。"); }
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
