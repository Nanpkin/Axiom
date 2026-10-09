using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private async Task ChooseTaskTemplateAsync()
    {
        if (busy || activeDialog is not null || !reachable || status is null) return;
        var templates = AutomationTemplates.Build(status);
        if (templates.Length == 0) { ShowNotice("当前后台没有可用的常用场景，请刷新状态。"); return; }
        var body = Column(12); var visible = templates;
        var search = new TextBox { Header = "查找场景", PlaceholderText = "例如：提醒、窗口、麦克风", MaxLength = 80 };
        var choices = new ComboBox { Header = "选择想完成的事情", ItemsSource = templates.Select(t => t.Name).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var details = Text(templates[0].Description, 14, Muted);

        body.Children.Add(search); body.Children.Add(choices); body.Children.Add(details);
        body.Children.Add(Text("这是未保存、默认暂停的草稿，不会执行。打开编辑器后仍需填写所需内容并选择对象，再审阅、保存并启用；所有参数沿用正常校验。", 12, Muted));
        var dialog = new ContentDialog { Title = "从常用场景开始", Content = body, PrimaryButtonText = "打开暂停草稿", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot };
        void UpdateSelection()
        {
            var selected = choices.SelectedIndex >= 0 && choices.SelectedIndex < visible.Length;
            dialog.IsPrimaryButtonEnabled = selected;
            details.Text = selected ? visible[choices.SelectedIndex].Description : "没有匹配的场景，请换一个关键词。";
        }
        choices.SelectionChanged += (_, _) => UpdateSelection();
        search.TextChanged += (_, _) =>
        {
            var previous = choices.SelectedIndex >= 0 && choices.SelectedIndex < visible.Length ? visible[choices.SelectedIndex].Id : null;
            var query = search.Text.Trim();
            visible = templates.Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            choices.ItemsSource = visible.Select(t => t.Name).ToArray();
            var index = Array.FindIndex(visible, t => t.Id == previous);
            choices.SelectedIndex = index >= 0 ? index : visible.Length > 0 ? 0 : -1;
            UpdateSelection();
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && choices.SelectedIndex >= 0 && choices.SelectedIndex < visible.Length)
            SelectDraft(visible[choices.SelectedIndex].Draft);
    }
}
