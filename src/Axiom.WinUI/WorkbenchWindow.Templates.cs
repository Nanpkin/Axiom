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
        var body = Column(12);
        var choices = new ComboBox { Header = "选择想完成的事情", ItemsSource = templates.Select(t => t.Name).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var details = Text(templates[0].Description, 14, Muted);
        choices.SelectionChanged += (_, _) => { if (choices.SelectedIndex >= 0) details.Text = templates[choices.SelectedIndex].Description; };
        body.Children.Add(choices); body.Children.Add(details);
        body.Children.Add(Text("这是未保存、默认暂停的草稿，不会执行。打开编辑器后仍需选择程序、目录或快捷键，再审阅、保存并启用；所有参数沿用正常校验。", 12, Muted));
        var dialog = new ContentDialog { Title = "从常用场景开始", Content = body, PrimaryButtonText = "打开暂停草稿", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && choices.SelectedIndex >= 0) SelectDraft(templates[choices.SelectedIndex].Draft);
    }
}
