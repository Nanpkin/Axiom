namespace Axiom.Providers.Windows;

internal sealed record MonitorRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => checked(Right - Left);
    public int Height => checked(Bottom - Top);
}
internal sealed record DesktopMonitor(nint Handle, MonitorRectangle Bounds, MonitorRectangle Work, bool Primary);

internal static class WindowMonitorGeometry
{
    public static DesktopMonitor[] Order(IEnumerable<DesktopMonitor> monitors)
    {
        var ordered = monitors.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ThenBy(m => m.Bounds.Right)
            .ThenBy(m => m.Bounds.Bottom).ThenBy(m => m.Handle).ToArray();
        if (ordered.Length is < 1 or > 64 || ordered.Select(m => m.Handle).Distinct().Count() != ordered.Length ||
            ordered.Count(m => m.Primary) != 1 || ordered.Any(m => m.Handle == 0 || m.Bounds.Width < 2 || m.Bounds.Height < 2 ||
                m.Work.Width < 2 || m.Work.Height < 2 || m.Work.Left < m.Bounds.Left || m.Work.Top < m.Bounds.Top ||
                m.Work.Right > m.Bounds.Right || m.Work.Bottom > m.Bounds.Bottom)) throw new ArgumentException("显示器布局无法确认。");
        return ordered;
    }
    public static DesktopMonitor Select(IReadOnlyCollection<DesktopMonitor> monitors, nint source, string direction)
    {
        var ordered = Order(monitors);
        if (direction == "primary") return ordered.Single(m => m.Primary);
        if (direction is not ("next" or "previous")) throw new ArgumentException("目标显示器选项无效。");
        var index = Array.FindIndex(ordered, m => m.Handle == source);
        if (index < 0) throw new ArgumentException("当前窗口所在显示器已变化。");
        return ordered[(index + (direction == "next" ? 1 : ordered.Length - 1)) % ordered.Length];
    }
    public static (int X, int Y, int Width, int Height) Center(DesktopMonitor monitor) =>
        WindowLayoutGeometry.Calculate("center", monitor.Work.Left, monitor.Work.Top, monitor.Work.Width, monitor.Work.Height);
    public static bool SameTopology(IEnumerable<DesktopMonitor> first, IEnumerable<DesktopMonitor> second) => Order(first).SequenceEqual(Order(second));
}
