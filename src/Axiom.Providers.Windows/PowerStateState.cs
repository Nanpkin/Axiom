using System.Globalization;

namespace Axiom.Providers.Windows;

public sealed record PowerStateSnapshot(byte AcLine, byte BatteryFlag, byte BatteryPercent);
public sealed record PowerStateValue(string ResourceId, bool? Satisfied);

public static class PowerStateState
{
    public static PowerStateValue[] Samples(string[] resources, PowerStateSnapshot? status) =>
        [.. resources.Select(id => new PowerStateValue(id, Evaluate(id, status)))];
    public static bool? Evaluate(string resource, PowerStateSnapshot? status)
    {
        if (status is null) return null;
        var ac = status.AcLine;
        if (resource == "ac") return ac is 0 or 1 ? ac == 1 : null;
        var noBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) != 0;
        if (noBattery) return ac == 0 ? null : false; // battery supply with no battery is inconsistent
        if (ac == 1) return false;
        if (ac != 0 || status.BatteryFlag == 255) return null;
        if (resource == "battery") return true;
        var percent = status.BatteryPercent;
        return percent <= 100 ? percent < int.Parse(resource[14..], CultureInfo.InvariantCulture) : null;
    }
}
