namespace Axiom.Providers.Windows;

public sealed record WifiProfileValue(string ProfileName, bool? Connected);
public sealed record WifiInterfaceValue(Guid Id, bool Known, string? ConnectedProfile);

// Device identifiers stay in this local projection. A failed interface can never prove disconnection.
public sealed class WifiProfileState(string[] profiles)
{
    // Not-ready, ad-hoc formation and an in-progress disconnect are not proof of full disconnection.
    public static bool? ConnectionStatus(uint nativeState) => nativeState == 1 ? true : nativeState is >= 4 and <= 7 ? false : null;
    private readonly Dictionary<Guid, WifiInterfaceValue> interfaces = [];
    public void Replace(IEnumerable<WifiInterfaceValue> values)
    {
        var next = values.ToArray();
        if (next.Length > 64 || next.Select(v => v.Id).Distinct().Count() != next.Length) throw new IOException("Wi-Fi 网卡检测容量或身份无效。");
        interfaces.Clear(); foreach (var value in next) interfaces.Add(value.Id, value);
    }
    public bool Contains(Guid id) => interfaces.ContainsKey(id);
    public void Set(WifiInterfaceValue value)
    {
        if (!interfaces.ContainsKey(value.Id) && interfaces.Count >= 64) throw new IOException("Wi-Fi 网卡检测容量已满。");
        interfaces[value.Id] = value;
    }
    public WifiProfileValue[] Samples() => [.. profiles.Select(profile =>
    {
        var connected = interfaces.Values.Any(v => v.Known && string.Equals(v.ConnectedProfile, profile, StringComparison.Ordinal));
        var unknown = interfaces.Count == 0 || interfaces.Values.Any(v => !v.Known);
        return new WifiProfileValue(profile, connected ? true : unknown ? null : false);
    }).OrderByDescending(v => v.Connected == true).ThenByDescending(v => v.Connected is null)];
}
