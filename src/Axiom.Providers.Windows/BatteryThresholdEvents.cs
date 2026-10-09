namespace Axiom.Providers.Windows;

// The first power-setting notification establishes a baseline. Unknown values break it.
internal sealed class BatteryThresholdEvents
{
    private uint? previous;
    internal string[] Update(uint percentage)
    {
        var before = previous; previous = percentage <= 100 ? percentage : null;
        if (before is null || previous is null) return [];
        var events = new List<string>(2);
        if (before > 20 && percentage <= 20) events.Add("battery-low20");
        if (before > 10 && percentage <= 10) events.Add("battery-low10");
        return [.. events];
    }
}
