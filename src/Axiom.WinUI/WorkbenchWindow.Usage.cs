using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private UsageSnapshot? usageSnapshot;
    private NamedPipeClientStream? usageLease;
    private string? usageSelection;
    private int usageOffset;
    private readonly Button usageStop = Action("结束使用观察");
    private readonly TextBlock usageIndicator = Text("", 12, Amber);
    private CancellationTokenSource? usageExpiry;

    private async Task<AutomationMessage> UsageAsync(UsageRequest request)
    {
        var response = await WorkbenchClient.RequestAsync(new(1, "usage", Guid.NewGuid().ToString("N"), Usage: request));
        if (response.Type != "usage" || response.UsageSnapshot is null) throw new InvalidOperationException(response.Error ?? "观察状态未确认。");
        usageSnapshot = response.UsageSnapshot; UpdateUsageIndicator(); return response;
    }
    private void UpdateUsageIndicator()
    {
        var active = usageSnapshot?.Sessions.FirstOrDefault(s => s.Id == usageSnapshot.ActiveId);
        usageIndicator.Text = active is null ? "" : $"使用观察：{(active.State == "paused" ? "已暂停" : "已开启")} · {active.EndsUtc.ToLocalTime():HH:mm} 到期 · 关闭窗口即结束";
        usageIndicator.Visibility = usageStop.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task ReadUsageAsync() => await UsageAsync(new("read", SessionId: usageSelection, Offset: usageOffset));
    private async Task UsageCommandAsync(string operation, string? id = null, string? feedback = null, string? text = null)
    {
        if (busy) return; SetBusy(true);
        try
        {
            await UsageAsync(new(operation, SessionId: id ?? usageSelection, Text: text, FeedbackId: feedback, Offset: usageOffset));
            if (operation is "stop" or "clear" || operation == "delete" && usageSnapshot?.ActiveId is null) { usageLease?.Dispose(); usageLease = null; usageExpiry?.Cancel(); }
        }
        catch (Exception error) { ShowNotice(error.Message); }
        finally { SetBusy(false); Render(); }
    }
    private void BuildUsage()
    {
        content.Children.Add(Text("仅在你开启后记录应用元数据；不采集窗口标题、正文、按键、剪贴板、截图或命令行。关闭此窗口立即结束观察；自动化规则继续工作。", 14, Muted));
        content.Children.Add(Text("最多 120 分钟、4096 条事件、128 个应用；本地最多保留 12 次、7 天。未知来源不能当作手动操作。", 12, Muted));
        var actions = Row(8);
        var start = Action("开始观察", true); start.IsEnabled = reachable && usageSnapshot?.ActiveId is null; start.Click += async (_, _) => await StartUsageAsync(); actions.Children.Add(start);
        var refresh = Action("刷新状态"); refresh.Click += async (_, _) => await UsageCommandAsync("read"); actions.Children.Add(refresh);
        var active = usageSnapshot?.Sessions.FirstOrDefault(s => s.Id == usageSnapshot.ActiveId);
        if (active is not null)
        {
            var pause = Action(active.State == "paused" ? "继续" : "暂停"); pause.Click += async (_, _) => await UsageCommandAsync(active.State == "paused" ? "resume" : "pause"); actions.Children.Add(pause);
            var stop = Action("结束"); stop.Click += async (_, _) => await UsageCommandAsync("stop"); actions.Children.Add(stop);
        }
        content.Children.Add(actions);
        if (usageSnapshot is null) { content.Children.Add(Text("尚未读取观察状态。", 13, Muted)); return; }
        content.Children.Add(Text($"状态读取于 {usageSnapshot.ReadAtUtc.ToLocalTime():HH:mm:ss}，无定时轮询。", 12, Muted));
        var sessions = new ComboBox { Header = "本地观察记录", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = usageSnapshot.Sessions.Select(s => $"{s.StartedUtc.ToLocalTime():MM-dd HH:mm} · {StateName(s.State)}").ToArray() };
        var selected = usageSnapshot.Selected;
        sessions.SelectedIndex = Array.FindIndex(usageSnapshot.Sessions, s => s.Id == selected?.Id);
        sessions.SelectionChanged += async (_, _) => { if (sessions.SelectedIndex < 0 || busy) return; usageSelection = usageSnapshot.Sessions[sessions.SelectedIndex].Id; usageOffset = 0; await UsageCommandAsync("read"); };
        content.Children.Add(sessions);
        if (selected is null) { content.Children.Add(Text("没有观察记录。先开启一次你愿意记录的使用过程。", 14, Muted)); return; }
        content.Children.Add(Text("本次目的：" + (selected.Options.Purpose.Length == 0 ? "未填写" : selected.Options.Purpose), 13, Muted));
        foreach (var gap in selected.Gaps) content.Children.Add(Text(gap, 12, Amber));
        var tools = Row(8);
        var export = Action("预览分析摘要…"); export.Click += async (_, _) => await PreviewUsageExportAsync(selected.Id); tools.Children.Add(export);
        var import = Action("导入 AI 建议…"); import.Click += async (_, _) => await ImportUsageAsync(selected.Id); tools.Children.Add(import);
        content.Children.Add(tools);
        foreach (var suggestion in usageSnapshot.Suggestions ?? [])
        {
            var card = Column(8); card.Children.Add(Text(suggestion.Title + (suggestion.Kind == "existing" ? " · 可审阅规则" : " · 证据不足"), 17, Ink, true));
            card.Children.Add(Text(string.Join(" → ", suggestion.ObjectIds.Select(id => LocalName(selected, id))), 14));
            card.Children.Add(Text(suggestion.Evidence, 13)); card.Children.Add(Text(suggestion.Limitation, 12, Muted));
            if (suggestion.Kind == "existing")
            {
                var accept = Action("确认意图并审阅规则…", true);
                accept.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync("这是你希望自动完成的准备工作吗？", suggestion.Limitation, "生成暂停的草稿")) return;
                    try { var result = await UsageAsync(new("draft", SessionId: selected.Id, FeedbackId: suggestion.Id)); page = Page.Rules; SelectUsageDraft(result.Task!); }
                    catch (Exception error) { ShowNotice(error.Message); }
                };
                card.Children.Add(accept);
            }
            var feedback = Row(8); var ignore = Action("忽略"); ignore.Click += async (_, _) => await UsageCommandAsync("feedback", selected.Id, suggestion.Id, "ignored"); feedback.Children.Add(ignore);
            var irrelevant = Action("不符合需求"); irrelevant.Click += async (_, _) => await UsageCommandAsync("feedback", selected.Id, suggestion.Id, "irrelevant"); feedback.Children.Add(irrelevant);
            var extension = Action("补充需求…"); extension.Click += async (_, _) => await CreateUsageExtensionAsync(selected.Id); feedback.Children.Add(extension);
            card.Children.Add(feedback); content.Children.Add(Card(card));
        }
        if (usageSnapshot.Suggestions is not { Length: > 0 }) content.Children.Add(Text("目前没有足够重复证据，或建议已处理。可使用摘要与 AI 分析，不会自动配置规则。", 13, Muted));
        var records = Column(4); records.Children.Add(Text($"记录 {usageOffset + (selected.Events.Length > 0 ? 1 : 0)}–{usageOffset + selected.Events.Length} / {usageSnapshot.TotalEvents}", 15, Ink, true));
        foreach (var e in selected.Events) records.Children.Add(Text($"{e.AtUtc.ToLocalTime():HH:mm:ss} · {EventName(e.Kind)} · {LocalName(selected, e.ObjectId)} · {(e.Origin == "axiom" ? "Axiom" : "来源未知")}", 12, Muted));
        content.Children.Add(Card(records));
        var pages = Row(8); var before = Action("上一页"); before.IsEnabled = usageOffset > 0; before.Click += async (_, _) => { usageOffset = Math.Max(0, usageOffset - 64); await UsageCommandAsync("read"); }; pages.Children.Add(before);
        var next = Action("下一页"); next.IsEnabled = usageOffset + 64 < usageSnapshot.TotalEvents; next.Click += async (_, _) => { usageOffset += 64; await UsageCommandAsync("read"); }; pages.Children.Add(next); content.Children.Add(pages);
        var removal = Row(8); var delete = Action("删除本次记录"); delete.Click += async (_, _) => { usageOffset = 0; await UsageCommandAsync("delete", selected.Id); }; removal.Children.Add(delete);
        var clear = Action("清空记录"); clear.Click += async (_, _) => { if (await ConfirmAsync("清空使用观察？", "全部观察与相关反馈将被删除；现有规则保留。", "清空")) { usageOffset = 0; usageSelection = null; await UsageCommandAsync("clear"); } }; removal.Children.Add(clear); content.Children.Add(removal);
    }
    private static string StateName(string state) => state switch { "recording" => "观察中", "paused" => "已暂停", _ => "已结束" };
    private static string EventName(string kind) => kind switch { "foreground" => "前台切换", "process-start" => "程序启动", "axiom-action" => "Axiom 发起动作", _ => "记录间断" };
    private static string LocalName(UsageSession session, string? id) => session.Objects.FirstOrDefault(o => o.Id == id) is { } item ? Path.GetFileName(item.Executable) : "—";

    private async Task StartUsageAsync()
    {
        if (busy || activeDialog is not null) return;
        var body = Column(12); var foreground = new CheckBox { Content = "前台应用切换（不含窗口标题）", IsChecked = true }; var starts = new CheckBox { Content = "程序启动（不含命令行）", IsChecked = true };
        var duration = new ComboBox { Header = "持续时间", ItemsSource = new[] { "5 分钟", "15 分钟", "30 分钟", "60 分钟", "120 分钟" }, SelectedIndex = 1 };
        var exclusions = new TextBox { Header = "排除应用：每行一个 exe 文件名", AcceptsReturn = true, Height = 80, MaxLength = 17000 };
        var purpose = new TextBox { Header = "我准备做什么（选填，会进入导出预览）", MaxLength = 500 };
        body.Children.Add(foreground); body.Children.Add(starts); body.Children.Add(duration); body.Children.Add(exclusions); body.Children.Add(purpose);
        body.Children.Add(Text("记录只留本机；关闭窗口即停止，重启不会自动恢复。Axiom 动作会单独标记，其他事件来源未知。", 12, Muted));
        var dialog = new ContentDialog { Title = "主动开启使用观察", Content = body, PrimaryButtonText = "开始", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        SetBusy(true);
        try
        {
            var options = new UsageOptions(new[] { 5, 15, 30, 60, 120 }[duration.SelectedIndex], foreground.IsChecked == true, starts.IsChecked == true,
                exclusions.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), purpose.Text);
            UsageValidation.Options(options);
            usageLease?.Dispose(); var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException();
            var lease = LocalUserPipe.Create(AutomationProtocol.PipeName(sid)); usageLease = lease;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await lease.ConnectForUserAsync(3000, timeout.Token);
            var id = Guid.NewGuid().ToString("N");
            await AutomationProtocol.WriteAsync(lease, new(1, "usage", id, DateTimeOffset.UtcNow.AddSeconds(12), Usage: new("start", options)), timeout.Token);
            var response = await AutomationProtocol.ReadAsync(lease, timeout.Token);
            if (response.RequestId != id || response.Version != 1 || response.Type != "usage" || response.UsageSnapshot is null) throw new InvalidOperationException(response.Error ?? "开启结果未确认。");
            usageSnapshot = response.UsageSnapshot; usageSelection = usageSnapshot.ActiveId; usageOffset = 0; UpdateUsageIndicator();
            usageExpiry?.Cancel(); usageExpiry?.Dispose(); usageExpiry = new(); _ = RefreshAtUsageDeadlineAsync(TimeSpan.FromMinutes(options.Minutes), usageExpiry.Token);
        }
        catch (Exception error) { usageLease?.Dispose(); usageLease = null; ShowNotice(error.Message); }
        finally { SetBusy(false); Render(); }
    }
    private async Task RefreshAtUsageDeadlineAsync(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token); DispatcherQueue.TryEnqueue(async () => { if (!closed) await UsageCommandAsync("read"); }); }
        catch (OperationCanceledException) { }
    }
    private async Task PreviewUsageExportAsync(string id)
    {
        try { var response = await UsageAsync(new("export", SessionId: id)); await PreviewUsageTextAsync("摘要预览与删减（64 KiB 上限）", response.ExportText!); }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private async Task PreviewUsageTextAsync(string title, string text)
    {
        var input = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 65536, Height = 380 };
        var body = Column(8); body.Children.Add(Text("可删除不愿分享的段落。只有点击复制才写入剪贴板；不会上传。对象映射与完整路径仍留本机。", 12, Muted)); body.Children.Add(input);
        var dialog = new ContentDialog { Title = title, Content = body, PrimaryButtonText = "复制确认后的内容", CloseButtonText = "取消", XamlRoot = shell.XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (Encoding.UTF8.GetByteCount(input.Text) > UsageValidation.MaximumExportBytes) throw new ArgumentException("内容超过 64 KiB，请继续删减。");
        var data = new DataPackage(); data.SetText(input.Text); Clipboard.SetContent(data); ShowNotice("已复制你确认的内容。", true);
    }
    private async Task ImportUsageAsync(string id)
    {
        var input = new TextBox { AcceptsReturn = true, Height = 320, MaxLength = 65536, PlaceholderText = "粘贴结构化 JSON 建议" };
        var dialog = new ContentDialog { Title = "导入建议（不执行）", Content = input, PrimaryButtonText = "校验并预览", CloseButtonText = "取消", XamlRoot = shell.XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var response = await UsageAsync(new("import", SessionId: id, Text: input.Text));
            if (response.Task is { } draft)
            {
                if (!await ConfirmAsync("审阅候选规则", response.ExportText + "\n将进入规则编辑器，默认暂停；必须保存并启用才可能执行。", "打开编辑器")) return;
                page = Page.Rules; SelectUsageDraft(draft);
            }
            else if (response.ExportText?.StartsWith("extension：", StringComparison.Ordinal) == true)
            {
                if (await ConfirmAsync("生成扩展需求", response.ExportText + "\n请确认此需求是你希望解决的问题。", "生成任务包"))
                {
                    var package = await UsageAsync(new("extension", SessionId: id, Text: input.Text)); await PreviewUsageTextAsync("扩展开发任务预览", package.ExportText!);
                }
            }
            else await ConfirmAsync("证据不足", response.ExportText ?? "还需要更多观察或意图说明。", "知道了");
        }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private void SelectUsageDraft(Axiom.Domain.AutomationTask draft)
    {
        editor.LoadDraft(draft); editing = true; notice.IsOpen = false; Render();
    }
    private async Task CreateUsageExtensionAsync(string id)
    {
        var title = new TextBox { Header = "希望解决什么问题", MaxLength = 120 };
        var description = new TextBox { Header = "操作前后对比、希望自动完成的步骤（不填路径或个人信息）", AcceptsReturn = true, Height = 180, MaxLength = 2000 };
        var body = Column(10); body.Children.Add(title); body.Children.Add(description);
        var dialog = new ContentDialog { Title = "补充并确认扩展需求", Content = body, PrimaryButtonText = "生成任务包", CloseButtonText = "取消", XamlRoot = shell.XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var json = JsonSerializer.Serialize(new { Version = 1, SessionId = id, Kind = "extension", Title = title.Text, Explanation = description.Text });
            var result = await UsageAsync(new("extension", SessionId: id, Text: json)); await PreviewUsageTextAsync("扩展开发任务预览", result.ExportText!);
        }
        catch (Exception error) { ShowNotice(error.Message); }
    }
}
