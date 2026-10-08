namespace Axiom.Contracts;

// Configuration discovery only; choosing a row stores its full executable path, never a transient HWND/UID.
public sealed record TrayCatalogEntry(string ExecutablePath, string DisplayName, int IconCount);
public sealed record TrayCatalog(TrayCatalogEntry[] Entries, DateTimeOffset ReadAtUtc, string? Notice = null);

public static class TrayCatalogValidation
{
    public static void Validate(TrayCatalog catalog)
    {
        if (catalog.Entries is null || catalog.Entries.Length > 128 || catalog.Notice?.Length > 2000 ||
            catalog.Entries.Any(e => e is null || e.DisplayName is null || e.DisplayName.Length is < 1 or > 160 || e.IconCount is < 1 or > 64) ||
            catalog.Entries.Select(e => e.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Entries.Length)
            throw new InvalidDataException("托盘名单无效。");
        foreach (var entry in catalog.Entries) ExecutablePaths.ValidatePath(entry.ExecutablePath);
    }
}
