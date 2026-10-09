using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public interface ISessionDisplaySource : IDisposable
{
    ChannelReader<string> Events { get; }
    bool Failed => false;
}
public sealed record SessionDisplaySample(uint? State, bool Baseline);

// There is no synchronous screen-power query. Only the documented notification payload establishes Known.
public sealed class SessionDisplayState
{
    private uint? previous;
    public static uint? Decode(string kind) => kind switch { "screen-on" => 1, "screen-dim" => 2, "screen-off" => 0, _ => null };
    public static bool? Evaluate(string resource, uint? state) => state is <= 2 ? resource switch
    {
        "on" => state == 1, "dim" => state == 2, "off" => state == 0, _ => throw new ArgumentException("屏幕状态资源无效。")
    } : null;
    public SessionDisplaySample? Read(uint? value)
    {
        var current = value is <= 2 ? value : null;
        if (current == previous) return null;
        var baseline = previous is null && current.HasValue;
        previous = current;
        return new(current, baseline);
    }
}
