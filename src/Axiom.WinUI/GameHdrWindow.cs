using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Axiom.WinUI;

public sealed class GameHdrWindow : Window
{
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    private static readonly SolidColorBrush Ink = Brush(25, 39, 56);
    private static readonly SolidColorBrush Muted = Brush(89, 105, 121);
    private static readonly SolidColorBrush Accent = Brush(61, 92, 206);
    private static readonly SolidColorBrush Success = Brush(22, 120, 78);
    private static readonly SolidColorBrush Warning = Brush(160, 104, 25);
    private static readonly SolidColorBrush Failure = Brush(180, 54, 54);
    private sealed record RuleRow(GameHdrRuleDto Rule)
    {
        public string Summary => $"{Rule.Title}   ·   {(Rule.ProcessNamesText ?? Rule.ProcessName).Replace("\n", "、")}   ·   HDR {(Rule.HdrEnabled ? "开" : "关")}   ·   {(Rule.Paused ? "已暂停" : "运行中")}";
    }
    private readonly ListView rules = new() { Height = 175, DisplayMemberPath = nameof(RuleRow.Summary), SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBox title = new() { PlaceholderText = "例如：原神", MaxLength = 120 };
    private readonly TextBox process = new() { PlaceholderText = "每行一个 .exe 文件名", MaxLength = 4096,
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 112 };
    private readonly ComboBox display = new() { PlaceholderText = "选择显示器", MinWidth = 260, DisplayMemberPath = nameof(GameHdrDisplayDto.Name) };
    private readonly ToggleSwitch target = new() { Header = "游戏运行时打开 HDR", IsOn = true };
    private readonly ToggleSwitch paused = new() { Header = "暂停这条规则" };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = Ink };
    private readonly TextBlock reason = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Muted };
    private readonly TextBlock connection = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Muted };
    private readonly Button save = new() { Content = "保存并启用", MinHeight = 42 };
    private readonly Button refresh = new() { Content = "刷新结果", MinHeight = 42 };
    private readonly Button fresh = new() { Content = "新建规则", MinHeight = 42 };
    private readonly Button delete = new() { Content = "删除所选规则", MinHeight = 42 };
    private string id = Guid.NewGuid().ToString("D");
    private long version;
    private string? selectedPath;
    private bool applying;
    private bool busy;
    private bool loaded;
    private bool closed;

    public GameHdrWindow()
    {
        Title = "Axiom · 游戏 HDR 自动化";
        AppWindow.Resize(new SizeInt32(940, 830));
        Content = Build();
        rules.SelectionChanged += (_, _) => { if (!applying && rules.SelectedItem is RuleRow item) Select(item.Rule); };
        display.SelectionChanged += (_, _) => { if (!applying && display.SelectedItem is GameHdrDisplayDto item) selectedPath = item.Path; };
        fresh.Click += (_, _) => NewRule();
        refresh.Click += async (_, _) => await RefreshAsync();
        save.Click += async (_, _) => await SaveAsync();
        delete.Click += async (_, _) => await DeleteAsync();
        Closed += (_, _) => closed = true;
        Activated += async (_, _) => { if (!loaded) { loaded = true; await RefreshAsync(); } };
    }

    private UIElement Build()
    {
        var page = new StackPanel { Spacing = 17, Padding = new Thickness(28), MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Center };
        page.Children.Add(new TextBlock { Text = "AXIOM  /  AUTOMATIONS", FontSize = 13,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Accent });
        page.Children.Add(new TextBlock { Text = "游戏 HDR", FontSize = 34,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Ink });
        page.Children.Add(new TextBlock { Text = "启动指定游戏时切换 HDR；游戏退出后恢复之前的状态。关闭此窗口，后台仍会继续运行。",
            FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = Muted });

        var statusCard = Card(Brush(235, 241, 255));
        var statusBody = new StackPanel { Spacing = 8 };
        statusBody.Children.Add(new TextBlock { Text = "最近结果", FontSize = 13, Foreground = Accent,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 } });
        statusBody.Children.Add(result);
        statusBody.Children.Add(reason);
        statusBody.Children.Add(connection);
        statusCard.Child = statusBody;
        page.Children.Add(statusCard);

        var listCard = Card(Brush(255, 255, 255));
        var listBody = new StackPanel { Spacing = 9 };
        listBody.Children.Add(Heading("已保存的规则"));
        listBody.Children.Add(new TextBlock { Text = "选择一条规则可修改 HDR 目标或暂停。", Foreground = Muted, FontSize = 14 });
        listBody.Children.Add(rules);
        listCard.Child = listBody;
        page.Children.Add(listCard);

        var editor = Card(Brush(255, 255, 255));
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(Heading("规则设置"));
        fields.Children.Add(Label("名称")); fields.Children.Add(title);
        fields.Children.Add(Label("游戏进程名")); fields.Children.Add(process);
        fields.Children.Add(new TextBlock { Text = "每行一个 .exe；任意一个启动时执行，全部退出后恢复。安装位置变化后无需修改。",
            Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(Label("切换哪台显示器")); fields.Children.Add(display);
        fields.Children.Add(target);
        fields.Children.Add(paused);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        save.Background = Accent; save.Foreground = Brush(255, 255, 255);
        buttons.Children.Add(save); buttons.Children.Add(fresh); buttons.Children.Add(refresh); buttons.Children.Add(delete);
        fields.Children.Add(new ScrollViewer { Content = buttons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        editor.Child = fields;
        page.Children.Add(editor);
        page.Children.Add(new TextBlock { Text = "同时运行的游戏若要求相反 HDR 状态，Axiom 会停止切换并显示冲突原因。游戏原有的内置 HDR 选项仍需在游戏内设置。",
            FontSize = 13, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Background = Brush(247, 249, 253), Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static Border Card(Brush background) => new()
    {
        Background = background, CornerRadius = new CornerRadius(18), Padding = new Thickness(22),
        BorderBrush = Brush(224, 231, 240), BorderThickness = new Thickness(1)
    };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 21, Foreground = Ink,
        FontWeight = new Windows.UI.Text.FontWeight { Weight = 650 } };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 14, Foreground = Ink };

    private void NewRule()
    {
        applying = true;
        rules.SelectedItem = null;
        id = Guid.NewGuid().ToString("D"); version = 0;
        title.Text = ""; process.Text = ""; target.IsOn = true; paused.IsOn = false;
        applying = false;
        result.Text = "新规则尚未保存";
        reason.Text = "填写进程名、显示器和 HDR 目标后保存。";
    }

    private void Select(GameHdrRuleDto item)
    {
        applying = true;
        id = item.Id; version = item.Version;
        title.Text = item.Title; process.Text = item.ProcessNamesText ?? item.ProcessName;
        target.IsOn = item.HdrEnabled; paused.IsOn = item.Paused;
        selectedPath = item.DisplayPath;
        display.SelectedItem = (display.ItemsSource as IEnumerable<GameHdrDisplayDto> ?? [])
            .FirstOrDefault(x => x.Path == item.DisplayPath);
        applying = false;
    }

    private async Task RefreshAsync()
    {
        if (busy || closed) return;
        busy = true; UpdateButtons();
        try
        {
            var reply = await RequestAsync(new(1, "status", Guid.NewGuid().ToString("N")));
            if (reply.Type != "status" || reply.Status is null) throw new InvalidDataException(reply.Error ?? "无法读取结果。 ");
            ApplyStatus(reply.Status);
        }
        catch (Exception error) { result.Text = "后台暂不可用"; reason.Text = error.Message; }
        finally { busy = false; UpdateButtons(); }
    }

    private async Task SaveAsync()
    {
        if (busy || closed) return;
        if (selectedPath is null) { result.Text = "请先选择显示器"; return; }
        busy = true; UpdateButtons();
        try
        {
            var names = process.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var edit = new GameHdrRuleDto(id, version, title.Text.Trim(), names.FirstOrDefault() ?? "",
                selectedPath, target.IsOn, paused.IsOn, string.Join("\n", names));
            var reply = await RequestAsync(new(1, "save", Guid.NewGuid().ToString("N"), Rule: edit));
            if (reply.Type != "saved" || reply.Rule is null || reply.Status is null)
                throw new InvalidDataException(reply.Error ?? "保存失败。 ");
            id = reply.Rule.Id; version = reply.Rule.Version;
            ApplyStatus(reply.Status);
            result.Text = paused.IsOn ? "规则已保存并暂停" :
                reply.Status.AgentConnected ? "规则已保存，后台监控已更新" : "规则已保存，等待后台连接";
        }
        catch (Exception error) { result.Text = "保存未确认"; reason.Text = error.Message; }
        finally { busy = false; UpdateButtons(); }
    }

    private async Task DeleteAsync()
    {
        if (busy || closed || version == 0) return;
        busy = true; UpdateButtons();
        try
        {
            var reply = await RequestAsync(new(1, "delete", Guid.NewGuid().ToString("N"),
                Id: id, ExpectedVersion: version));
            if (reply.Type != "deleted" || reply.Status is null)
                throw new InvalidDataException(reply.Error ?? "删除失败。 ");
            NewRule();
            ApplyStatus(reply.Status);
            result.Text = "规则已删除";
        }
        catch (Exception error) { result.Text = "删除未确认"; reason.Text = error.Message; }
        finally { busy = false; UpdateButtons(); }
    }

    private void ApplyStatus(GameHdrStatusDto status)
    {
        result.Text = status.State;
        result.Foreground = status.State is "已切换" or "已恢复" or "无需切换" ? Success :
            status.State.Contains("失败", StringComparison.Ordinal) || status.State.Contains("异常", StringComparison.Ordinal) ? Failure :
            status.State is "规则冲突" or "未切换" or "等待下次启动" ? Warning : Ink;
        reason.Text = status.Reason;
        connection.Text = status.AgentConnected ? "后台检测正在运行 · 最近更新 " + status.AtUtc.ToLocalTime().ToString("HH:mm:ss")
            : "会话程序尚未连接，规则暂时不会执行";
        applying = true;
        var current = selectedPath;
        display.ItemsSource = status.Displays.Where(x => x.Supported == true).ToArray();
        display.SelectedItem = status.Displays.FirstOrDefault(x => x.Path == current);
        var rows = status.Rules.Select(x => new RuleRow(x)).ToArray();
        rules.ItemsSource = rows;
        rules.SelectedItem = rows.FirstOrDefault(x => x.Rule.Id == id);
        applying = false;
    }

    private void UpdateButtons() { save.IsEnabled = fresh.IsEnabled = refresh.IsEnabled = !busy; delete.IsEnabled = !busy && version > 0; }

    private static async Task<GameHdrMessage> RequestAsync(GameHdrMessage request)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("No user SID.");
        using var pipe = new NamedPipeClientStream(".", GameHdrProtocol.HostPipe(sid), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        await pipe.ConnectForUserAsync(3000, timeout.Token);
        await GameHdrProtocol.WriteAsync(pipe, request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(12) }, timeout.Token);
        var reply = await GameHdrProtocol.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
        if (reply.Version != 1 || reply.Minor != 0 || reply.Schema != 1 || reply.Scope != "user-session" ||
            reply.RequestId != request.RequestId) throw new InvalidDataException("后台响应无效。 ");
        return reply;
    }
}
