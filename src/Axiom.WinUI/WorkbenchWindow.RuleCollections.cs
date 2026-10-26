using Axiom.Contracts;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private RuleCollections ruleCollections = RuleCollections.Empty;
    private bool collectionsLoaded;
    private string? collectionsError;
    private static string CollectionsPath => Path.Combine(AxiomUserData.Root, "ui-rule-collections.json");

    private void LoadRuleCollections()
    {
        if (collectionsLoaded) return;
        try { ruleCollections = RuleCollections.Load(CollectionsPath); collectionsLoaded = true; collectionsError = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { collectionsError = "分类暂时无法读取。原分类文件已保留，规则仍正常运行；刷新后重试。"; }
    }

    private bool SaveRuleCollections(RuleCollections next)
    {
        if (!collectionsLoaded) { ShowNotice(collectionsError ?? "分类尚未读取，请刷新重试。"); return false; }
        try { next.Save(CollectionsPath); ruleCollections = next; Render(); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowNotice("分类更改未保存，请重试。原分类保持不变。"); return false; }
    }

    private UIElement BuildCollectionStrip()
    {
        var layout = new Grid { ColumnSpacing = 8 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tabs = Row(6);
        void Add(string id, string name, int count)
        {
            var selected = ruleCollections.Selected == id;
            var button = Action(name + "  " + count);
            button.MinHeight = 34; button.Padding = new Thickness(12, 5, 12, 5);
            button.Background = selected ? Soft : Canvas; button.Foreground = selected ? Accent : Muted;
            button.BorderThickness = new Thickness(0);
            AutomationProperties.SetName(button, name + "，" + count + " 条规则" + (selected ? "，已选中" : ""));
            button.Click += (_, _) => { if (id != ruleCollections.Selected) SaveRuleCollections(ruleCollections with { Selected = id }); };
            tabs.Children.Add(button);
        }
        var tasks = status?.Tasks ?? [];
        Add("", "全部", tasks.Length);
        Add(RuleCollections.Unfiled, "未分类", tasks.Count(x => ruleCollections.CollectionOf(x.Id) == RuleCollections.Unfiled));
        foreach (var collection in ruleCollections.Collections)
            Add(collection.Id, collection.Name, tasks.Count(x => ruleCollections.CollectionOf(x.Id) == collection.Id));
        layout.Children.Add(new ScrollViewer { Content = tabs, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var manage = RuleIconButton("\uE8B7", "管理分类"); manage.IsEnabled = collectionsLoaded;
        var menu = new MenuFlyout(); manage.Flyout = menu;
        var create = new MenuFlyoutItem { Text = "新建分类", Icon = new SymbolIcon(Symbol.Add) };
        create.Click += async (_, _) => await EditCollectionAsync(null); menu.Items.Add(create);
        var current = ruleCollections.Collections.FirstOrDefault(x => x.Id == ruleCollections.Selected);
        if (current is not null)
        {
            var rename = new MenuFlyoutItem { Text = "重命名当前分类" };
            rename.Click += async (_, _) => await EditCollectionAsync(current); menu.Items.Add(rename);
            var remove = new MenuFlyoutItem { Text = "删除当前分类" };
            remove.Click += async (_, _) =>
            {
                if (await ConfirmAsync("删除“" + current.Name + "”？", "其中的规则会移到“未分类”，规则和运行状态保持不变。", "删除分类"))
                    SaveRuleCollections(ruleCollections.Remove(current.Id));
            };
            menu.Items.Add(remove);
        }
        Grid.SetColumn(manage, 1); layout.Children.Add(manage);
        return layout;
    }

    private async Task EditCollectionAsync(RuleCollection? collection)
    {
        var input = new TextBox { Header = "分类名称", PlaceholderText = "例如：工作、游戏、日常", MaxLength = 24, Text = collection?.Name ?? "" };
        var error = Text("", 12, Amber); var panel = Column(10); panel.Children.Add(input); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = shell.XamlRoot, Title = collection is null ? "新建分类" : "重命名分类",
            Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var next = ruleCollections.Rename(collection?.Id, input.Text);
                if (!SaveRuleCollections(next)) { args.Cancel = true; error.Text = "未能保存分类，请重试。"; }
            }
            catch (ArgumentException failure) { args.Cancel = true; error.Text = failure.Message; }
        };
        await ShowDialogAsync(dialog);
    }

    private static Button RuleIconButton(string glyph, string label)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Muted },
            Width = 32, Height = 32, Padding = new Thickness(7), CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label); return button;
    }

    private Button BuildRuleMenu(AutomationTask task, string detail)
    {
        var button = RuleIconButton("\uE712", "更多操作：" + task.Title);
        var menu = new MenuFlyout(); button.Flyout = menu;
        var information = new MenuFlyoutItem { Text = "规则详情", Icon = new FontIcon { Glyph = "\uE946" } };
        information.Click += async (_, _) => await ShowDialogAsync(new ContentDialog { XamlRoot = shell.XamlRoot,
            Title = task.Title, CloseButtonText = "关闭", Content = new ScrollViewer { MaxHeight = 360,
                Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } } });
        menu.Items.Add(information);
        var move = new MenuFlyoutSubItem { Text = "移至分类", IsEnabled = collectionsLoaded };
        void Add(string id, string name)
        {
            var item = new ToggleMenuFlyoutItem { Text = name, IsChecked = ruleCollections.CollectionOf(task.Id) == id };
            item.Click += (_, _) => SaveRuleCollections(ruleCollections.Move(task.Id, id)); move.Items.Add(item);
        }
        Add(RuleCollections.Unfiled, "未分类");
        foreach (var collection in ruleCollections.Collections) Add(collection.Id, collection.Name);
        menu.Items.Add(move); return button;
    }
}
