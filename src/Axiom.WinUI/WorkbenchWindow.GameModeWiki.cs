using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private void BuildGameModeWiki()
    {
        // Keep one user-facing source, embedded so the installed app works offline.
        using var stream = typeof(WorkbenchWindow).Assembly.GetManifestResourceStream("Axiom.WinUI.GameModeWiki.md");
        if (stream is null)
        {
            content.Children.Add(Text("游戏模式说明暂不可用，请重新打开完整应用。", 14, Amber));
            return;
        }
        using var reader = new StreamReader(stream);
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            content.Children.Add(Text(string.Join(" ", paragraph), 14, Muted));
            paragraph.Clear();
        }
        // This article uses only headings, paragraphs and bullet items; no external renderer.
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# ")) { Flush(); continue; }
            if (line.StartsWith("## ")) { Flush(); content.Children.Add(Text(line[3..], 17, Ink, true)); }
            else if (line.StartsWith("- ")) { Flush(); content.Children.Add(Text("•  " + line[2..], 14, Muted)); }
            else if (string.IsNullOrWhiteSpace(line)) Flush();
            else paragraph.Add(line);
        }
        Flush();
    }
}
