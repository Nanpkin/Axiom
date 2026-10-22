using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    // Local navigation within Toolbox/Settings; refresh keeps the current detail open.
    private string? detailPage;

    private void OpenDetail(string name)
    {
        if (busy) return;
        detailPage = name; pageScrollToTop = true; notice.IsOpen = false; Render();
    }

    private void DetailHeader(string name)
    {
        pageTitle.Text = name;
        pageSubtitle.Text = page == Page.Toolbox ? "工具箱" : "设置";
        content.MaxWidth = 780;
        var back = Action(page == Page.Toolbox ? "← 工具箱" : "← 设置");
        back.Click += (_, _) => { detailPage = null; pageScrollToTop = true; Render(); };
        var actions = Row(8); actions.Children.Add(back); actions.Children.Add(RefreshButton());
        pageTools.Children.Add(actions);
    }

    private void BuildToolbox()
    {
        if (detailPage is not null)
        {
            DetailHeader(detailPage);
            switch (detailPage)
            {
                case "Steam 游戏整理": BuildSteamTool(); break;
                case "托盘管理": BuildTrayTool(); break;
                case "滚轮调节亮度": BuildBrightnessWheelTool(); break;
                case "定时 Hosts": BuildHostsTool(); break;
            }
            return;
        }
        var tiles = new Grid { ColumnSpacing = 12, RowSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Left };
        (string Name, string Glyph, string Hint)[] entries =
        [
            ("Steam 游戏整理", "\uE7FC", "自动整理本机 Steam 游戏进程组"),
            ("托盘管理", "\uE73F", "选择需要隐藏的程序托盘图标"),
            ("滚轮调节亮度", "\uE706", "用快捷键和滚轮调整亮度"),
            ("定时 Hosts", "\uE774", "按检测条件更新域名解析记录")
        ];
        foreach (var entry in entries)
        {
            var icon = new FontIcon { Glyph = entry.Glyph, FontSize = 23, Foreground = Accent };
            var caption = Text(entry.Name, 12, Ink);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            var face = Column(9); face.HorizontalAlignment = HorizontalAlignment.Center;
            face.Children.Add(icon); face.Children.Add(caption);
            var button = new Button { Content = face, Width = 132, Height = 92,
                Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
                HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTipService.SetToolTip(button, entry.Hint);
            AutomationProperties.SetName(button, entry.Name);
            button.Click += (_, _) => OpenDetail(entry.Name);
            tiles.Children.Add(button);
        }
        void Fit()
        {
            var columns = Math.Clamp((int)(content.ActualWidth / 144), 1, entries.Length);
            tiles.ColumnDefinitions.Clear(); tiles.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) tiles.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            for (var i = 0; i < (entries.Length + columns - 1) / columns; i++) tiles.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var i = 0; i < tiles.Children.Count; i++)
            { Grid.SetColumn((FrameworkElement)tiles.Children[i], i % columns); Grid.SetRow((FrameworkElement)tiles.Children[i], i / columns); }
        }
        content.SizeChanged += (_, _) => Fit(); Fit();
        content.Children.Add(tiles);
    }

    private void BuildSettings()
    {
        if (detailPage is not null)
        {
            DetailHeader(detailPage);
            switch (detailPage)
            {
                case "外观": BuildThemeSettings(); break;
                case "游戏模式": BuildGameModeSettings(); break;
                case "快捷键": BuildShortcutSettings(); break;
                case "登录启动": BuildStartupSettings(); break;
                case "停止与退出": BuildStopSettings(); break;
            }
            return;
        }
        content.Spacing = 0;
        content.MaxWidth = 780;
        AddSettingsEntry("外观", "\uE790", "跟随系统、浅色或深色");
        AddSettingsEntry("游戏模式", "\uE7FC", "手动与按程序自动开启");
        AddSettingsEntry("快捷键", "\uE765", "管理全局按键组合");
        AddSettingsEntry("登录启动", "\uE7E8", "登录 Windows 后在后台运行");
        AddSettingsEntry("停止与退出", "\uE711", "停止所有任务并退出后台");
        pageTools.Children.Add(RefreshButton());
    }

    private void AddSettingsEntry(string name, string glyph, string description)
    {
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, Foreground = Accent });
        var text = Column(3); text.Children.Add(Text(name, 14, Ink, true));
        text.Children.Add(Text(description, 12, Muted)); Grid.SetColumn(text, 1); row.Children.Add(text);
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 12, Foreground = Muted };
        Grid.SetColumn(chevron, 2); row.Children.Add(chevron);
        var button = new Button { Content = row, Background = Surface, BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 12, 16, 12), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => { OpenDetail(name); if (name == "游戏模式") await OpenGameModeAsync(); };
        content.Children.Add(button);
        content.Children.Add(new Border { Height = 1, Background = Line });
    }
}
