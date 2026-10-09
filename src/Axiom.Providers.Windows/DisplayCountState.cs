using System.Globalization;

namespace Axiom.Providers.Windows;

public sealed record DisplayCountValue(string ResourceId, bool? Satisfied);

public static class DisplayCountState
{
    public const int MaximumMonitors = 64;
    public static int? ConfirmCount(int count, bool complete) => complete && count is >= 1 and <= MaximumMonitors ? count : null;
    public static DisplayCountValue[] Samples(string[] resources, int? count) => [.. resources.Select(id => new DisplayCountValue(id, Evaluate(id, count)))];
    public static bool? Evaluate(string resource, int? count)
    {
        if (count is not (>= 1 and <= MaximumMonitors)) return null;
        var separator = resource.IndexOf(':'); var expected = int.Parse(resource[(separator + 1)..], CultureInfo.InvariantCulture);
        return resource[..separator] switch { "at-least" => count >= expected, "exact" => count == expected, _ => throw new ArgumentException("显示器数量条件无效。") };
    }
}
