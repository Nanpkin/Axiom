using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private ProcessGroup[] processGroups = [];
    private string? groupsError;
    private bool groupEditing;
    private bool viewingProcessGroups;
    private ProcessGroup? groupOriginal, groupDraft;
    private bool GroupDirty => groupDraft is not null && (groupOriginal is null || groupOriginal.Name != groupDraft.Name ||
        !groupOriginal.Entries.SequenceEqual(groupDraft.Entries));

    private UIElement BuildProcessPicker(string? value, Action<string> changed, bool includeGroups = true, bool executablePaths = false, bool currentTray = false)
    {
        var selection = new ProcessSelectionState(value, executablePaths);
        var panel = Column(12); var selected = Column(6); var count = Text("", 13, Ink, true);
        panel.Children.Add(count);
        panel.Children.Add(new ScrollViewer { Content = selected, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var checks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase); var syncing = false;
        void Update(bool notify)
        {
            count.Text = $"已选择 {selection.Names.Count} 个程序";
            selected.Children.Clear();
            if (selection.Names.Count == 0) selected.Children.Add(Text(currentTray ? "从当前托盘勾选程序，或手动添加完整路径。" : executablePaths ? "批量输入完整路径，或多选程序文件。" : "从组中选择，或添加程序文件。", 12, Muted));
            foreach (var item in selection.Names.ToArray())
            {
                var row = Row(10); var remove = Action("移除"); remove.MinHeight = 28; remove.Padding = new Thickness(9, 3, 9, 3);
                row.Children.Add(Text(item, 13)); row.Children.Add(remove);
                remove.Click += (_, _) => { selection.Set([item], false); Update(true); }; selected.Children.Add(row);
            }
            syncing = true; foreach (var (name, checkbox) in checks) checkbox.IsChecked = selection.Contains(name); syncing = false;
            if (notify) changed(selection.Value);
        }
        void Set(string[] names, bool enabled)
        {
            try { selection.Set(names, enabled); Update(true); }
            catch (ArgumentException error) { ShowNotice(error.Message); Update(false); }
        }
        if (includeGroups)
        {
            var groupPicker = new ComboBox { Header = "从进程组选择", PlaceholderText = "选择一个组…", ItemsSource = processGroups,
                DisplayMemberPath = nameof(ProcessGroup.Name), HorizontalAlignment = HorizontalAlignment.Stretch };
            var options = Column(4); var actions = Row(10); var all = Action("全选组内程序"); var clear = Action("取消组内选择");
            all.IsEnabled = clear.IsEnabled = false;
            actions.Children.Add(all); actions.Children.Add(clear);
            groupPicker.SelectionChanged += (_, _) =>
            {
                options.Children.Clear(); checks.Clear(); var group = groupPicker.SelectedItem as ProcessGroup;
                all.IsEnabled = clear.IsEnabled = group is not null && group.Entries.Length > 0;
                if (group is null) return;
                foreach (var entry in group.Entries)
                {
                    var checkbox = new CheckBox { Content = entry.DisplayName == entry.ProcessName ? entry.ProcessName : $"{entry.DisplayName} · {entry.ProcessName}",
                        IsChecked = selection.Contains(entry.ProcessName) };
                    checkbox.Checked += (_, _) => { if (!syncing) Set([entry.ProcessName], true); };
                    checkbox.Unchecked += (_, _) => { if (!syncing) Set([entry.ProcessName], false); };
                    checks[entry.ProcessName] = checkbox; options.Children.Add(checkbox);
                }
            };
            all.Click += (_, _) => { if (groupPicker.SelectedItem is ProcessGroup group) Set(group.Entries.Select(x => x.ProcessName).ToArray(), true); };
            clear.Click += (_, _) => { if (groupPicker.SelectedItem is ProcessGroup group) Set(group.Entries.Select(x => x.ProcessName).ToArray(), false); };
            panel.Children.Add(groupPicker); panel.Children.Add(actions);
            panel.Children.Add(new ScrollViewer { Content = options, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (processGroups.Length == 0) panel.Children.Add(Text(groupsError ?? "还没有进程组。在左侧‘组’中创建，或先手动添加。", 12, Muted));
            panel.Children.Add(Text("选择后名单独立保存；修改组不会改变已有任务。", 12, Muted));
        }
        var add = Action("批量输入名单…"); add.Click += async (_, _) =>
        {
            if (await ShowBatchProcessInputAsync(selection, executablePaths)) Update(true);
        };
        var browse = Action("选择程序文件…"); browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add(".exe");
                var files = await picker.PickMultipleFilesAsync();
                if (files.Count > 0) Set(files.Select(x => executablePaths ? x.Path : System.IO.Path.GetFileName(x.Path)).ToArray(), true);
            }
            catch (Exception error) { ShowNotice("无法选择程序：" + error.Message); }
        };
        if (currentTray)
        {
            var tray = Action("从当前托盘选择…", true);
            tray.Click += async (_, _) => { if (await ShowTrayPickerAsync(selection)) Update(true); };
            panel.Children.Add(tray);
        }
        var manualActions = Row(10); manualActions.Children.Add(add); manualActions.Children.Add(browse);
        panel.Children.Add(manualActions); Update(false); return panel;
    }
    private async Task<bool> ShowBatchProcessInputAsync(ProcessSelectionState selection, bool executablePaths = false, bool exclusions = false)
    {
        var input = new TextBox { Header = executablePaths ? "程序完整路径" : "程序文件名", PlaceholderText = executablePaths ? "C:\\Apps\\One.exe\nD:\\Apps\\Two.exe" : "yuanshen.exe\nre4.exe", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MaxLength = 32768, MinHeight = 180, MaxHeight = 320 };
        var body = Column(12); body.Children.Add(Text(executablePaths ? "每行填写一个本机 .exe 完整路径，最多 32 个；重复路径自动合并。" :
            exclusions ? "每行填写一个 .exe 文件名；即使当前未扫描到，以后也不会自动导入。" : "每行填写一个 .exe 文件名，重复名称会自动合并。", 13, Muted));
        body.Children.Add(input); var error = Text("", 12, Amber); body.Children.Add(error);
        var dialog = new ContentDialog { Title = exclusions ? "批量添加排除项" : "批量添加程序", Content = body, PrimaryButtonText = exclusions ? "加入排除项" : "添加到名单",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = shell.XamlRoot };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var names = ProcessGroups.SplitNames(input.Text);
                if (names.Length == 0) throw new ArgumentException(executablePaths ? "请至少填写一个程序完整路径。" : "请至少填写一个程序文件名。");
                selection.Set(names, true);
            }
            catch (ArgumentException exception) { error.Text = exception.Message; args.Cancel = true; }
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }
    private void BuildGroups()
    {
        if (groupEditing && groupDraft is not null)
        {
            var back = Action("← 返回进程组"); back.Click += async (_, _) => { if (await ConfirmDiscardAsync()) { groupEditing = false; Render(); } };
            content.Children.Add(back);
            var name = new TextBox { Header = "组名称", MaxLength = 80, PlaceholderText = "例如 HDR 游戏、常用工具", Text = groupDraft.Name };
            name.TextChanged += (_, _) => groupDraft = groupDraft! with { Name = name.Text }; content.Children.Add(name);
            content.Children.Add(Card(BuildProcessPicker(string.Join("\n", groupDraft.Entries.Select(x => x.ProcessName)), value =>
            {
                var old = groupDraft!.Entries;
                groupDraft = groupDraft with { Entries = [.. ProcessGroups.SplitNames(value).Select(x =>
                    old.FirstOrDefault(e => e.ProcessName.Equals(x, StringComparison.OrdinalIgnoreCase)) ?? new ProcessGroupEntry(x, x))] };
            }, false)));
            var save = Action("保存进程组", true); save.Click += async (_, _) => await SaveGroupAsync(groupDraft!, true);
            footer.Children.Add(save);
            var cancel = Action("取消"); cancel.Click += async (_, _) => { if (await ConfirmDiscardAsync()) { groupEditing = false; Render(); } }; footer.Children.Add(cancel);
            if (groupDraft.Version > 0)
            {
                var delete = Action("删除组"); delete.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync("删除这个进程组？", "已有任务中的程序名单会保留。", "删除")) return;
                    SetBusy(true);
                    try
                    {
                        var response = await WorkbenchClient.RequestAsync(new(1, "delete-group", Guid.NewGuid().ToString("N"), Id: groupDraft!.Id, ExpectedVersion: groupDraft.Version));
                        if (response.Type != "groups" || response.Groups is null) throw new InvalidOperationException(response.Error ?? "删除未确认。");
                        processGroups = response.Groups; groupEditing = false; Render(); ShowNotice("进程组已删除。", true);
                    }
                    catch (Exception error) { ShowNotice(error.Message); } finally { SetBusy(false); }
                };
                footer.Children.Add(delete);
            }
            return;
        }
        if (!viewingProcessGroups)
        {
            content.Children.Add(Text("选择组类型", 14, Muted));
            content.Children.Add(BuildCatalogEntry("进程组", "整理常用程序，在任务中全选或按需勾选。", "\uE8F1", "管理进程组", () =>
            { viewingProcessGroups = true; Render(); }, $"已保存 {processGroups.Length} 个组"));
            if (groupsError is not null) content.Children.Add(Text(groupsError, 13, Amber));
            return;
        }
        var returnHome = Action("← 返回组类型"); returnHome.Click += (_, _) => { viewingProcessGroups = false; Render(); }; content.Children.Add(returnHome);
        var header = Column(10); header.Children.Add(Text("进程组", 20, Ink, true));
        header.Children.Add(Text("把常用程序整理成组，创建任务时即可全选或单独勾选。", 13, Muted));
        var create = Action("＋ 新建进程组", true); create.Click += (_, _) => EditGroup(null); header.Children.Add(create);
        content.Children.Add(Card(header, Soft));
        if (groupsError is not null) content.Children.Add(Text(groupsError, 13, Amber));
        if (processGroups.Length == 0) content.Children.Add(Text("还没有进程组。手动添加程序，或到工具箱导入 Steam 游戏。", 14, Muted));
        foreach (var group in processGroups)
        {
            var body = Column(10); body.Children.Add(Text(group.Name, 18, Ink, true)); body.Children.Add(Text($"{group.Entries.Length} 个程序", 12, Muted));
            body.Children.Add(Text(string.Join(" · ", group.Entries.Take(5).Select(x => x.DisplayName)) + (group.Entries.Length > 5 ? " …" : ""), 13, Muted));
            if (group.AutoManaged)
            {
                body.Children.Add(Text("由 Steam 工具自动维护；排除项在工具设置中管理。", 12, Muted));
                var settings = Action("工具设置"); settings.Click += async (_, _) => { page = Page.Toolbox; viewingProcessGroups = false; Render(); await OpenSteamSettingsAsync(); }; body.Children.Add(settings);
            }
            else { var edit = Action("编辑组"); edit.Click += (_, _) => EditGroup(group); body.Children.Add(edit); }
            content.Children.Add(Card(body));
        }
    }
    private void EditGroup(ProcessGroup? group)
    {
        groupOriginal = group; groupDraft = group ?? new(Guid.NewGuid().ToString("D"), 0, "", []);
        groupEditing = true; notice.IsOpen = false; Render();
    }
    private async Task<bool> SaveGroupAsync(ProcessGroup group, bool leaveEditor)
    {
        if (busy) return false;
        SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "save-group", Guid.NewGuid().ToString("N"), Group: group));
            if (response.Type != "groups" || response.Groups is null) throw new InvalidOperationException(response.Error ?? "保存未确认。");
            processGroups = response.Groups; groupsError = null;
            if (leaveEditor) groupEditing = false;
            Render(); ShowNotice("进程组已保存。已有任务保持原名单。", true); return true;
        }
        catch (Exception error) { ShowNotice(error.Message); return false; }
        finally { SetBusy(false); }
    }
    private async Task ReadGroupsAsync()
    {
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "read-groups", Guid.NewGuid().ToString("N")));
            if (response.Type != "groups" || response.Groups is null) throw new InvalidOperationException();
            processGroups = response.Groups; groupsError = null;
        }
        catch (Exception) { groupsError = "进程组未确认，请刷新后重试。"; }
    }
}
