using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public interface ISessionPresenceSource : IDisposable
{
    ChannelReader<string> Events { get; }
    bool Failed => false;
}
public sealed record SessionPresenceSample(uint? State, bool Baseline);
public sealed class SessionPresenceState
{
    private uint? previous;
    // GUID_SESSION_USER_PRESENCE carries USER_ACTIVITY_PRESENCE, not POWER_USER_PRESENCE.
    public static uint? Decode(string kind) => kind switch { "presence-present" => 0, "presence-absent" => 1, "presence-inactive" => 2, _ => null };
    public static bool? Evaluate(string resource, uint? state) => state is not (0 or 1 or 2) ? null : resource switch
    {
        "present" => state == 0, "absent" => state == 1, "inactive" => state == 2, _ => throw new ArgumentException("无效用户在场状态。")
    };
    public SessionPresenceSample? Read(uint? value)
    {
        var current = value is 0 or 1 or 2 ? value : null;
        if (current == previous) return null;
        var baseline = previous is null && current.HasValue;
        previous = current;
        return new(current, baseline);
    }
}
