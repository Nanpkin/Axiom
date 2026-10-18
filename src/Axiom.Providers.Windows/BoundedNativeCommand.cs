namespace Axiom.Providers.Windows;

internal sealed class BoundedNativeCommand(string name, TimeSpan timeout)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object state = new();
    private Task active = Task.CompletedTask;
    internal Task DrainAsync() { lock (state) return active; }
    internal async Task<(bool Success, string Reason)> RunAsync(Func<CancellationToken, (bool Success, string Reason)> action, CancellationToken token, TimeProvider? timeProvider = null, TimeSpan? operationTimeout = null)
    {
        token.ThrowIfCancellationRequested();
        if (!gate.Wait(0)) return (false, $"上一次{name}操作仍未结束，本次未执行；不排队或创建更多原生线程。");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workerToken = lifetime.Token; Task<(bool Success, string Reason)> work;
        lock (state) { work = Task.Factory.StartNew(() =>
        {
            try { workerToken.ThrowIfCancellationRequested(); return action(workerToken); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { return (false, $"{name}操作未确认完成；系统可能不可用、权限不足或操作已取消。不自动重试。"); }
            finally { gate.Release(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); active = work; }
        try { return await work.WaitAsync(operationTimeout ?? timeout, timeProvider ?? TimeProvider.System, token); }
        catch (TimeoutException) { return (false, $"{name}操作超时，可能已经执行但结果未确认；不自动重试。"); }
        finally { lifetime.Cancel(); }
    }
}
