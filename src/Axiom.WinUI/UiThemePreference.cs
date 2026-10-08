using Microsoft.UI.Xaml;
using Axiom.Contracts;

namespace Axiom.WinUI;

// Presentation preference only; automation configuration remains owned by the host.
internal static class UiThemePreference
{
    private static string FilePath => Path.Combine(AxiomUserData.Root, "ui-theme.txt");

    public static ElementTheme Load()
    {
        if (!File.Exists(FilePath)) return ElementTheme.Default;
        return File.ReadAllText(FilePath).Trim() switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    public static void Save(ElementTheme theme)
    {
        if (theme is not (ElementTheme.Default or ElementTheme.Light or ElementTheme.Dark))
            throw new ArgumentOutOfRangeException(nameof(theme));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, theme.ToString());
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
