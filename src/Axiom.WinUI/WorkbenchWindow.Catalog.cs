using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private Border BuildCatalogEntry(string title, string description, string glyph, string buttonLabel, Action open, string? detail = null)
    {
        var layout = new Grid { ColumnSpacing = 18, RowSpacing = 16 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var icon = new Border { Width = 48, Height = 48, Background = Soft, CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Top, Child = new FontIcon { Glyph = glyph, FontSize = 23, Foreground = Accent } };
        layout.Children.Add(icon);
        var body = Column(6); body.Children.Add(Text(title, 19, Ink, true)); body.Children.Add(Text(description, 13, Muted));
        if (detail is not null) body.Children.Add(Text(detail, 12, Muted)); Grid.SetColumn(body, 1); layout.Children.Add(body);
        var button = Action(buttonLabel, true); button.Click += (_, _) => open(); Grid.SetRow(button, 1); Grid.SetColumn(button, 1); layout.Children.Add(button);
        return Card(layout);
    }
}
