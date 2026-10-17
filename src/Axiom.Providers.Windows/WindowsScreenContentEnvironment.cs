using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// These are leases on the existing shared notification window, not additional
// windows or polling. The power subscription supplies its initial screen report.
public sealed class WindowsScreenContentEnvironment : IScreenContentEnvironment
{
    private readonly CancellationTokenSource stop = new();
    private readonly WindowsSessionEvents[] sources;
    private readonly Task[] pumps;
    private readonly PrimaryColorEvents color;
    private readonly Channel<string> events = Channel.CreateBounded<string>(64);
    public ChannelReader<string> Events => events.Reader;
    public WindowsScreenContentEnvironment()
    {
        var opened = new List<WindowsSessionEvents>();
        try
        {
            opened.Add(new(screenPowerOnly: true));
            opened.Add(new(lockStateOnly: true));
            opened.Add(new(displayOnly: true));
            opened.Add(new(clockOnly: true));
            sources = [.. opened];
            color = new(() => Write("display-color-changed"), error => events.Writer.TryComplete(error));
            pumps = [Pump(sources[0].Events), Pump(sources[1].Events), Pump(sources[2].Events), Clock(sources[3].ClockChanges)];
        }
        catch { foreach (var source in opened) source.Dispose(); stop.Dispose(); throw; }
    }
    public bool? ReadLocked(CancellationToken token) => WindowsSessionLock.ReadLocked(token);
    public IScreenContentSource Capture() => WindowsScreenContentCapture.Subscribe();
    private async Task Pump(ChannelReader<string> source)
    {
        try { await foreach (var kind in source.ReadAllAsync(stop.Token)) { if (kind == "display-changed") color.Refresh(); Write(kind); } if (!stop.IsCancellationRequested) throw new IOException("系统通知已停止。"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { events.Writer.TryComplete(error); }
    }
    private async Task Clock(ChannelReader<byte> source)
    {
        try { await foreach (var _ in source.ReadAllAsync(stop.Token)) Write("clock-changed"); if (!stop.IsCancellationRequested) throw new IOException("时钟通知已停止。"); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { events.Writer.TryComplete(error); }
    }
    private void Write(string kind)
    { if (!events.Writer.TryWrite(kind)) events.Writer.TryComplete(new IOException("系统通知拥塞，画面检测已暂停。")); }
    public void Dispose()
    {
        stop.Cancel(); foreach (var source in sources) source.Dispose(); color.Dispose();
        Task.WhenAll(pumps).GetAwaiter().GetResult(); stop.Dispose(); events.Writer.TryComplete();
    }
}
