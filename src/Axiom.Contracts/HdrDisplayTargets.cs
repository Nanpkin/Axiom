namespace Axiom.Contracts;

// Host observation and Agent execution resolve the same persisted named selection.
public static class HdrDisplayTargets
{
    private const string Prefix = "display-name:";
    public static string Named(string name) => Prefix + Uri.EscapeDataString(name.Trim().ToUpperInvariant());

    public static GameHdrDisplayDto[] NameSelections(IEnumerable<GameHdrDisplayDto> source)
    {
        var displays = source.ToArray();
        return displays.Select(display => displays.Count(d => string.Equals(d.Name.Trim(), display.Name.Trim(), StringComparison.OrdinalIgnoreCase)) == 1
            ? display with { Path = Named(display.Name), ConnectionPath = display.ConnectionPath ?? display.Path }
            : display with { ConnectionPath = display.ConnectionPath ?? display.Path }).ToArray();
    }

    public static GameHdrDisplayDto? Resolve(IEnumerable<GameHdrDisplayDto> source, string selection)
    {
        var displays = source.ToArray();
        GameHdrDisplayDto? Unique(Func<GameHdrDisplayDto, bool> match)
        { var found = displays.Where(match).Take(2).ToArray(); return found.Length == 1 ? found[0] : null; }
        if (selection.StartsWith(Prefix, StringComparison.Ordinal))
            return Unique(d => string.Equals(Named(d.Name), selection, StringComparison.OrdinalIgnoreCase));
        var exact = displays.Where(d => string.Equals(d.Path, selection, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.ConnectionPath, selection, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (exact.Length > 0) return exact.Length == 1 ? exact[0] : null;
        // Old rules only have a connection-instance address. Match its EDID model segment
        // to a unique current screen; an ambiguous model never selects another display.
        var model = Model(selection);
        return model is null ? null : Unique(d => string.Equals(Model(d.ConnectionPath ?? d.Path), model, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Model(string path)
    {
        if (!path.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = path.Split('#');
        return parts.Length == 4 && parts[1].Length > 0 && parts[2].Length > 0 ? parts[1] : null;
    }
}
