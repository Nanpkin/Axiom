namespace Axiom.Providers.Windows;

// Only used by the foreground command's short-lived event lease, before its one capture is bound.
internal sealed class CapturedWindowLifetime
{
    private readonly HashSet<nint> destroyed = [];
    private nint window;
    private int invalid;
    internal bool Invalid => Volatile.Read(ref invalid) != 0;
    // Caller serializes Bind/Destroy using the existing window event hub gate.
    internal void Bind(nint captured)
    {
        if (window != 0 || captured == 0) throw new ArgumentException("捕获窗口无效或已绑定。");
        window = captured;
        if (destroyed.Contains(captured)) Volatile.Write(ref invalid, 1);
        destroyed.Clear();
    }
    internal void Destroy(nint value)
    {
        if (window != 0) { if (window == value) Volatile.Write(ref invalid, 1); return; }
        if (destroyed.Count >= 256) { Volatile.Write(ref invalid, 1); return; }
        destroyed.Add(value);
    }
}
