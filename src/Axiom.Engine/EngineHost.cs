namespace Axiom.Engine;

/// <summary>Composition root for the pure core. Ports are borrowed, not owned.</summary>
public sealed partial class EngineHost : IDisposable
{
    private readonly object gate = new();
    private readonly IEngineClock clock;
    private bool running;
    private bool disposed;
    internal IDeadlineScheduler Scheduler { get; }
    internal ICapabilityExecutor Executor { get; }

    public EngineHost(IEngineClock clock, IDeadlineScheduler scheduler, ICapabilityExecutor executor)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public bool IsRunning { get { lock (gate) return running; } }

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running) return;
            RuntimeFault = null;
            running = true;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!running) return;
            running = false;
            StopCore();
        }
    }

    partial void StopCore();

    private void RequireRunning()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!running) throw new InvalidOperationException("Engine is stopped.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Stop();
            disposed = true;
        }
    }
}
