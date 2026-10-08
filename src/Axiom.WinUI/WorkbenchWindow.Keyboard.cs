using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private UIElement BuildHotkeyPicker(string? value, bool detection, Action<string> changed)
    {
        string Summary(string? input)
        {
            var parts = (input ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? "尚未添加组合键" : string.Join("　·　", parts.Take(3)) + (parts.Length > 3 ? $"　等 {parts.Length} 个" : "");
        }
        var panel = Column(10); var summary = Text(Summary(value), 13);
        panel.Children.Add(summary);
        var edit = Action(detection ? "设置触发快捷键…" : "设置发送组合键…"); panel.Children.Add(edit);
        edit.Click += async (_, _) =>
        {
            var draft = (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var body = Column(14); var modifiers = Row(12);
            var ctrl = new CheckBox { Content = "Ctrl", IsChecked = detection }; var alt = new CheckBox { Content = "Alt", IsChecked = detection };
            var shift = new CheckBox { Content = "Shift" }; var win = new CheckBox { Content = "Win" };
            foreach (var box in new[] { ctrl, alt, shift, win }) modifiers.Children.Add(box);
            body.Children.Add(modifiers);
            var key = new ComboBox { Header = "按键", ItemsSource = HotkeyCapabilities.Keys, SelectedItem = "A", HorizontalAlignment = HorizontalAlignment.Stretch };
            body.Children.Add(key);
            var add = Action("添加组合键"); body.Children.Add(add);
            var list = Column(8); body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var error = Text("", 12, Amber); body.Children.Add(error);
            void RenderDraft()
            {
                list.Children.Clear();
                for (var i = 0; i < draft.Count; i++)
                {
                    var index = i; var row = Row(8); row.Children.Add(Text(draft[i], 13));
                    if (!detection)
                    {
                        var up = Action("上移"); up.IsEnabled = i > 0;
                        up.Click += (_, _) => { (draft[index - 1], draft[index]) = (draft[index], draft[index - 1]); RenderDraft(); };
                        row.Children.Add(up);
                    }
                    var remove = Action("移除"); remove.Click += (_, _) => { draft.RemoveAt(index); RenderDraft(); }; row.Children.Add(remove); list.Children.Add(row);
                }
                if (draft.Count == 0) list.Children.Add(Text("添加一个或多个组合键。", 12, Muted));
            }
            void Add(string input)
            {
                try
                {
                    var proposed = draft.Concat(HotkeyCapabilities.ParseList(input, detection).Select(c => c.ToString()));
                    var validated = HotkeyCapabilities.ParseList(string.Join("\n", detection ? proposed.Distinct() : proposed), detection);
                    draft = validated.Select(c => c.ToString()).ToList(); error.Text = ""; RenderDraft();
                }
                catch (ArgumentException problem) { error.Text = problem.Message; }
            }
            add.Click += (_, _) =>
            {
                var flags = (ctrl.IsChecked == true ? ShortcutModifiers.Control : 0) | (alt.IsChecked == true ? ShortcutModifiers.Alt : 0) |
                    (shift.IsChecked == true ? ShortcutModifiers.Shift : 0) | (win.IsChecked == true ? ShortcutModifiers.Windows : 0);
                if (key.SelectedItem is string selected) Add(new ShortcutChord(flags, selected).ToString());
            };
            var bulk = Column(10); var input = new TextBox { Header = "组合键（每行一个）", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 1024, MinHeight = 90, PlaceholderText = "Ctrl + Alt + K\nCtrl + Shift + S" };
            var addBulk = Action("加入名单"); addBulk.Click += (_, _) => { Add(input.Text); };
            bulk.Children.Add(input); bulk.Children.Add(addBulk); body.Children.Add(new Expander { Header = "批量输入组合键…", Content = bulk });
            body.Children.Add(Text(detection ? "任意一个组合都能触发。至少含 Ctrl 或 Alt；按住不重复。" : "按列表顺序发送到当前前台窗口，可重复添加同一个组合。", 12, Muted));
            RenderDraft();
            var dialog = new ContentDialog { Title = detection ? "触发快捷键" : "发送组合键", Content = new ScrollViewer { Content = body, MaxHeight = 480 },
                PrimaryButtonText = "确认名单", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = shell.XamlRoot };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { HotkeyCapabilities.ParseList(string.Join("\n", draft), detection); }
                catch (ArgumentException problem) { error.Text = problem.Message; args.Cancel = true; }
            };
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            { value = string.Join("\n", HotkeyCapabilities.ParseList(string.Join("\n", draft), detection).Select(c => c.ToString())); summary.Text = Summary(value); changed(value); }
        };
        return panel;
    }
}
