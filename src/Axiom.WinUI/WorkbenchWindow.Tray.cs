using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private async Task<bool> ShowTrayPickerAsync(ProcessSelectionState selection)
    {
        var draft = new ProcessSelectionState(selection.Value, true);
        var body = Column(12); body.MaxWidth = 520;
        body.Children.Add(Text("读取当前可识别的托盘图标，包含折叠区。选中后保存实际注册图标的程序路径；同一程序的图标一起处理。", 13, Muted));
        var search = new TextBox { PlaceholderText = "搜索名称或程序路径", MaxLength = 160 };
        body.Children.Add(search);
        var actions = Row(10); var refresh = Action("刷新托盘"); var all = Action("全选搜索结果"); var clear = Action("取消搜索结果选择");
        actions.Children.Add(refresh); actions.Children.Add(all); actions.Children.Add(clear); body.Children.Add(actions);
        var status = Text("正在读取当前托盘…", 12, Muted); body.Children.Add(status);
        var rows = Column(6);
        var list = new ScrollViewer { Content = rows, MaxHeight = Math.Clamp(shell.ActualHeight - 420, 100, 260), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(list);
        SizeChangedEventHandler resized = (_, _) => list.MaxHeight = Math.Clamp(shell.ActualHeight - 420, 100, 260);
        var selected = Text("", 13, Ink, true); body.Children.Add(selected);
        var notice = Text("", 12, Muted); body.Children.Add(notice);
        var error = Text("", 12, Amber); body.Children.Add(error);
        var dialog = new ContentDialog { Title = "选择当前托盘", Content = body, PrimaryButtonText = "确认选择", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false, XamlRoot = shell.XamlRoot };
        TrayCatalogEntry[] entries = []; var loaded = false; var reading = false; var open = true;
        using var lifetime = new CancellationTokenSource();
        TrayCatalogEntry[] Visible() => entries.Where(e => e.DisplayName.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            e.ExecutablePath.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        void RenderRows()
        {
            rows.Children.Clear();
            foreach (var entry in Visible())
            {
                var detail = Column(3);
                detail.Children.Add(Text(entry.IconCount > 1 ? $"{entry.DisplayName} · {entry.IconCount} 个图标" : entry.DisplayName, 14, Ink, true));
                var path = Text(entry.ExecutablePath, 12, Muted); path.TextWrapping = TextWrapping.Wrap; detail.Children.Add(path);
                var check = new CheckBox { Content = detail, IsChecked = draft.Contains(entry.ExecutablePath), HorizontalAlignment = HorizontalAlignment.Stretch };
                check.Checked += (_, _) =>
                {
                    try { draft.Set([entry.ExecutablePath], true); error.Text = ""; }
                    catch (ArgumentException exception) { error.Text = exception.Message; check.IsChecked = false; }
                    selected.Text = $"动作名单共 {draft.Names.Count} 个程序";
                };
                check.Unchecked += (_, _) => { draft.Set([entry.ExecutablePath], false); selected.Text = $"动作名单共 {draft.Names.Count} 个程序"; };
                rows.Children.Add(check);
            }
            if (loaded && rows.Children.Count == 0) rows.Children.Add(Text(entries.Length == 0 ? "当前未发现可操作的托盘图标。可稍后刷新，或手动添加程序路径。" : "没有匹配的托盘。", 13, Muted));
            selected.Text = $"动作名单共 {draft.Names.Count} 个程序";
            all.IsEnabled = clear.IsEnabled = loaded && !reading && Visible().Length > 0;
        }
        async Task ReadAsync()
        {
            if (reading) return;
            reading = true; refresh.IsEnabled = all.IsEnabled = clear.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false;
            status.Text = "正在读取当前托盘…"; error.Text = "";
            try
            {
                var response = await WorkbenchClient.RequestAsync(new(1, "read-tray", Guid.NewGuid().ToString("N")), lifetime.Token);
                if (!open) return;
                if (response.Error is not null || response.Type != "tray" || response.Tray is null) throw new IOException(response.Error ?? "托盘名单未确认，请重试。");
                TrayCatalogValidation.Validate(response.Tray);
                entries = response.Tray.Entries; loaded = true;
                status.Text = $"{entries.Length} 个程序 · {entries.Sum(e => e.IconCount)} 个图标 · {response.Tray.ReadAtUtc.ToLocalTime():HH:mm:ss} 读取";
                notice.Text = response.Tray.Notice ?? "未显示的已选程序仍保留。";
            }
            catch (Exception exception) { if (open) { loaded = false; entries = []; status.Text = "读取失败"; error.Text = exception.Message; } }
            finally { if (open) { reading = false; refresh.IsEnabled = true; dialog.IsPrimaryButtonEnabled = loaded; RenderRows(); } }
        }
        search.TextChanged += (_, _) => RenderRows();
        refresh.Click += async (_, _) => await ReadAsync();
        all.Click += (_, _) => { try { draft.Set(Visible().Select(e => e.ExecutablePath), true); error.Text = ""; RenderRows(); } catch (ArgumentException exception) { error.Text = exception.Message; } };
        clear.Click += (_, _) => { draft.Set(Visible().Select(e => e.ExecutablePath), false); error.Text = ""; RenderRows(); };
        dialog.Opened += async (_, _) => { shell.SizeChanged += resized; await ReadAsync(); };
        dialog.Closed += (_, _) => { open = false; shell.SizeChanged -= resized; lifetime.Cancel(); };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!loaded || reading) { args.Cancel = true; return; }
            // Draft is validated as a whole before changing the caller's selection; Cancel never commits.
            selection.Set(selection.Names.ToArray(), false); selection.Set(draft.Names, true);
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }
}
