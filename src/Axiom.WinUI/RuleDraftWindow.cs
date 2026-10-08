using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Axiom.WinUI;

/// <summary>Edits inert rule drafts through the host. No database or rule executor is present here.</summary>
public sealed class RuleDraftWindow : Window
{
    private readonly ComboBox choices = new() { MinWidth = 290, PlaceholderText = "选择已有草稿" };
    private readonly TextBox title = new() { PlaceholderText = "例如：锁屏时降低亮度", MaxLength = 120 };
    private readonly TextBox condition = new() { PlaceholderText = "描述将来需要满足的条件", MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 95 };
    private readonly TextBox action = new() { PlaceholderText = "描述将来希望执行的动作", MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 95 };
    private readonly TextBlock status = new() { Text = "正在读取草稿…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock version = new() { Text = "新草稿 · 尚未保存" };
    private readonly Button save = new() { Content = "保存草稿", MinHeight = 44 };
    private readonly Button fresh = new() { Content = "新建", MinHeight = 44 };
    private readonly Button reload = new() { Content = "重新读取", MinHeight = 44 };
    private string currentId = Guid.NewGuid().ToString("D");
    private long currentVersion;
    private bool busy;
    private bool applying;
    private bool loaded;
    private bool closed;

    public RuleDraftWindow()
    {
        Title = "Axiom · 规则草稿";
        AppWindow.Resize(new SizeInt32(820, 730));
        Content = BuildContent();
        choices.DisplayMemberPath = nameof(RuleDraftSummary.Title);
        choices.SelectionChanged += async (_, _) =>
        {
            if (!applying && choices.SelectedItem is RuleDraftSummary selected) await GetAsync(selected.Id);
        };
        fresh.Click += (_, _) => NewDraft();
        reload.Click += async (_, _) =>
        {
            if (currentVersion > 0) await GetAsync(currentId);
            else await LoadListAsync();
        };
        save.Click += async (_, _) => await SaveAsync();
        title.TextChanged += (_, _) => Dirty();
        condition.TextChanged += (_, _) => Dirty();
        action.TextChanged += (_, _) => Dirty();
        Closed += (_, _) => closed = true;
        Activated += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            await LoadListAsync();
        };
        UpdateActions();
    }

    private UIElement BuildContent()
    {
        var body = new StackPanel { Spacing = 14, MaxWidth = 720, Padding = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(new TextBlock { Text = "规则草稿", FontSize = 30, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 } });
        body.Children.Add(new TextBlock { Text = "草稿 · 不可执行。当前没有对应的真实 Provider 和授权动作；以下文字只保存配置意图，不会触发任何系统操作。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "已有草稿" });
        body.Children.Add(choices);
        body.Children.Add(version);
        body.Children.Add(new TextBlock { Text = "名称" });
        body.Children.Add(title);
        body.Children.Add(new TextBlock { Text = "条件说明（仅文字，不执行）" });
        body.Children.Add(condition);
        body.Children.Add(new TextBlock { Text = "预期动作说明（仅文字，不执行）" });
        body.Children.Add(action);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(save);
        buttons.Children.Add(fresh);
        buttons.Children.Add(reload);
        body.Children.Add(buttons);
        body.Children.Add(status);
        AutomationProperties.SetName(title, "规则名称");
        AutomationProperties.SetName(condition, "条件说明");
        AutomationProperties.SetName(action, "预期动作说明");
        AutomationProperties.SetName(save, "保存规则草稿");
        AutomationProperties.SetName(reload, "重新读取规则草稿");
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void NewDraft()
    {
        if (busy) return;
        applying = true;
        choices.SelectedItem = null;
        currentId = Guid.NewGuid().ToString("D");
        currentVersion = 0;
        title.Text = condition.Text = action.Text = "";
        version.Text = "新草稿 · 尚未保存";
        status.Text = "填写后保存；此草稿不会执行。";
        applying = false;
    }

    private void Dirty()
    {
        if (!applying && !busy) status.Text = "有未保存更改。";
    }

    private async Task LoadListAsync()
    {
        if (busy || closed) return;
        busy = true;
        UpdateActions();
        try
        {
            var reply = await RequestAsync(new(1, 0, 1, "list", NewRequestId(), RuleDraftProtocol.Scope));
            if (reply.Type != "list" || reply.Items is null) throw Failure(reply);
            applying = true;
            choices.ItemsSource = reply.Items;
            applying = false;
            status.Text = $"已读取 {reply.Items.Length} 条草稿。草稿不可执行。";
        }
        catch (Exception exception) { status.Text = "读取失败：" + exception.Message; }
        finally { busy = false; UpdateActions(); }
    }

    private async Task GetAsync(string id)
    {
        if (busy || closed) return;
        busy = true;
        UpdateActions();
        try
        {
            var reply = await RequestAsync(new(1, 0, 1, "get", NewRequestId(), RuleDraftProtocol.Scope, Id: id));
            if (reply.Type != "draft" || reply.Draft is null || reply.Draft.Executable) throw Failure(reply);
            ApplyDraft(reply.Draft);
            status.Text = "已读取草稿；尚无执行能力。";
        }
        catch (Exception exception) { status.Text = "读取失败：" + exception.Message; }
        finally { busy = false; UpdateActions(); }
    }

    private async Task SaveAsync()
    {
        if (busy || closed) return;
        busy = true;
        var committed = false;
        status.Text = "保存中…";
        UpdateActions();
        try
        {
            var reply = await RequestAsync(new(1, 0, 1, "save", NewRequestId(), RuleDraftProtocol.Scope,
                Id: currentId, ExpectedVersion: currentVersion, Title: title.Text,
                ConditionText: condition.Text, ActionText: action.Text));
            if (reply.Type == "conflict")
            {
                status.Text = $"版本冲突：当前版本 {reply.CurrentVersion}。输入仍在此窗口，请重新读取后再编辑。";
                return;
            }
            if (reply.Type != "saved" || reply.Draft is null || reply.Draft.Executable) throw Failure(reply);
            committed = true;
            ApplyDraft(reply.Draft);
            status.Text = "已保存 · 草稿不可执行。";
            var summaries = (choices.ItemsSource as IEnumerable<RuleDraftSummary> ?? [])
                .Where(x => x.Id != reply.Draft.Id)
                .Append(new(reply.Draft.Id, reply.Draft.Version, reply.Draft.Title, reply.Draft.UpdatedAtUtc))
                .OrderByDescending(x => x.UpdatedAtUtc).ToArray();
            applying = true;
            choices.ItemsSource = summaries;
            choices.SelectedItem = summaries.First(x => x.Id == reply.Draft.Id);
            applying = false;
        }
        catch (Exception exception)
        {
            status.Text = committed
                ? "已保存，但列表更新失败；请重新读取：" + exception.Message
                : "保存失败；输入尚未确认写入：" + exception.Message;
        }
        finally { busy = false; UpdateActions(); }
    }

    private void ApplyDraft(RuleDraftDto draft)
    {
        applying = true;
        currentId = draft.Id;
        currentVersion = draft.Version;
        title.Text = draft.Title;
        condition.Text = draft.ConditionText;
        action.Text = draft.ActionText;
        version.Text = $"草稿 · 版本 {draft.Version} · 不可执行";
        applying = false;
    }

    private void UpdateActions()
    {
        save.IsEnabled = fresh.IsEnabled = reload.IsEnabled = !busy;
        choices.IsEnabled = !busy;
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    private static Exception Failure(RuleDraftMessage reply) =>
        new InvalidOperationException(reply.Error ?? reply.ErrorCode ?? "宿主返回了无效响应。");

    private static async Task<RuleDraftMessage> RequestAsync(RuleDraftMessage request)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("No OS user SID.");
        using var pipe = new NamedPipeClientStream(".", RuleDraftProtocol.PipeName(sid), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await pipe.ConnectForUserAsync(3000, timeout.Token);
        await RuleDraftProtocol.WriteAsync(pipe, request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10) }, timeout.Token);
        var reply = await RuleDraftProtocol.ReadAsync(pipe, timeout.Token)
            ?? throw new EndOfStreamException("宿主未返回结果。");
        if (reply.Major != 1 || reply.Minor != 0 || reply.Schema != 1 || reply.RequestId != request.RequestId ||
            reply.Scope != RuleDraftProtocol.Scope)
            throw new InvalidDataException("宿主响应版本或关联 ID 无效。");
        return reply;
    }
}
