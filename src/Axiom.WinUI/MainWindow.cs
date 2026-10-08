using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Axiom.WinUI;

public sealed class MainWindow : Window
{
    private enum ConnectionMode { Connecting, Observing, Paused, Disconnected }

    private static readonly SolidColorBrush Ink = Brush(28, 42, 61);
    private static readonly SolidColorBrush Muted = Brush(91, 106, 124);
    private static readonly SolidColorBrush Line = Brush(224, 231, 238);
    private static readonly SolidColorBrush Navy = Brush(24, 52, 78);
    private static readonly SolidColorBrush Green = Brush(18, 121, 91);
    private static readonly SolidColorBrush Amber = Brush(171, 109, 22);
    private static readonly SolidColorBrush Gray = Brush(101, 113, 127);

    private readonly TextBlock state = new() { Text = "未知", FontSize = 44, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock stateDescription = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly TextBlock connection = new() { FontSize = 15, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } };
    private readonly TextBlock lastQuery = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock statusBasis = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly Border errorPanel = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Background = Brush(255, 246, 233), Visibility = Visibility.Collapsed };
    private readonly Button start = new() { Content = "开始观察" };
    private readonly Button pause = new() { Content = "暂停观察" };
    private readonly Button retry = new() { Content = "重新连接 / 重试" };
    private NamedPipeClientStream? pipe;
    private CancellationTokenSource? readCancel;
    private readonly SemaphoreSlim writes = new(1, 1);
    private ConnectionMode mode = ConnectionMode.Connecting;
    private bool busy;
    private bool autoStarted;
    private bool closed;
    private string? connectionError;
    private RuleDraftWindow? draftWindow;
    private GameHdrWindow? gameWindow;

    public MainWindow()
    {
        Title = "Axiom · 自动化";
        AppWindow.Resize(new SizeInt32(960, 720));
        Content = BuildContent();
        AutomationProperties.SetName(start, "开始观察 Windows Event Log");
        AutomationProperties.SetName(pause, "暂停 Axiom 对 Windows Event Log 的观察");
        AutomationProperties.SetName(retry, "重新连接或重新建立观察");
        start.Click += async (_, _) => await StartAsync();
        pause.Click += async (_, _) => await PauseAsync();
        retry.Click += async (_, _) => await RetryAsync();
        Closed += (_, _) =>
        {
            closed = true;
            readCancel?.Cancel();
            pipe?.Dispose();
        };
        Activated += async (_, _) =>
        {
            if (autoStarted) return;
            autoStarted = true;
            await StartAsync();
        };
        ApplyState(null);
    }

    private UIElement BuildContent()
    {
        var page = new StackPanel { Spacing = 16, MaxWidth = 900, Padding = new Thickness(24, 22, 24, 28), HorizontalAlignment = HorizontalAlignment.Center };
        page.Children.Add(new TextBlock { Text = "AXIOM  /  本机自动化", FontSize = 14, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Navy });
        page.Children.Add(new TextBlock { Text = "让规则在后台运行", FontSize = 29, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Ink });
        page.Children.Add(new TextBlock { Text = "设置游戏启动时的 HDR 状态，并随时查看切换结果。", FontSize = 15, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        var gameCard = Card();
        gameCard.Background = Brush(235, 241, 255);
        var gameBody = new StackPanel { Spacing = 10 };
        gameBody.Children.Add(new TextBlock { Text = "游戏 HDR 自动化", FontSize = 23, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Navy });
        gameBody.Children.Add(new TextBlock { Text = "按进程名识别游戏，自动切换显示器 HDR；退出时恢复。支持保存与暂停。", TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 15 });
        var openGame = new Button { Content = "打开游戏 HDR 设置", MinHeight = 42, HorizontalAlignment = HorizontalAlignment.Left };
        SetActionStyle(openGame, Navy, Brush(255, 255, 255));
        openGame.Click += (_, _) =>
        {
            if (gameWindow is null)
            {
                gameWindow = new GameHdrWindow();
                gameWindow.Closed += (_, _) => gameWindow = null;
            }
            gameWindow.Activate();
        };
        gameBody.Children.Add(openGame);
        gameCard.Child = gameBody;
        page.Children.Add(gameCard);
        page.Children.Add(new TextBlock { Text = "Windows 服务状态", FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Ink });
        page.Children.Add(new TextBlock { Text = "以下是已有的 Event Log 服务观察功能。", FontSize = 14, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        var drafts = new Button { Content = "管理规则草稿", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(drafts, "打开规则草稿编辑器");
        drafts.Click += (_, _) =>
        {
            if (draftWindow is null)
            {
                draftWindow = new RuleDraftWindow();
                draftWindow.Closed += (_, _) => draftWindow = null;
            }
            draftWindow.Activate();
        };
        page.Children.Add(drafts);

        var overview = Card();
        var overviewBody = new StackPanel { Spacing = 9 };
        overviewBody.Children.Add(Label("状态总览"));
        overviewBody.Children.Add(new TextBlock { Text = "Windows Event Log", FontSize = 24, FontWeight = new Windows.UI.Text.FontWeight { Weight = 650 }, Foreground = Ink, TextWrapping = TextWrapping.Wrap });
        overviewBody.Children.Add(new TextBlock { Text = "服务名  EventLog", FontSize = 14, Foreground = Muted });
        overviewBody.Children.Add(state);
        overviewBody.Children.Add(stateDescription);
        overview.Child = overviewBody;
        var details = Card();
        var detailsBody = new StackPanel { Spacing = 12 };
        detailsBody.Children.Add(Label("观察详情"));
        detailsBody.Children.Add(Detail("观察连接", connection));
        detailsBody.Children.Add(Detail("最近成功查询", lastQuery));
        detailsBody.Children.Add(Detail("状态依据", statusBasis));
        detailsBody.Children.Add(Detail("范围", new TextBlock { Text = "这台计算机 · 当前用户的本地观察", Foreground = Ink, FontSize = 15, TextWrapping = TextWrapping.Wrap }));
        details.Child = detailsBody;
        var cards = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cards.Children.Add(overview);
        Grid.SetColumn(details, 1);
        cards.Children.Add(details);
        var stacked = false;
        page.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 700;
            if (narrow == stacked) return;
            stacked = narrow;
            cards.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(details, narrow ? 0 : 1);
            Grid.SetRow(details, narrow ? 1 : 0);
        };
        page.Children.Add(cards);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        SetActionStyle(start, Navy, Brush(255, 255, 255));
        SetActionStyle(pause, Navy, Brush(255, 255, 255));
        SetActionStyle(retry, Brush(230, 235, 241), Ink);
        actions.Children.Add(start);
        actions.Children.Add(pause);
        actions.Children.Add(retry);
        page.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = actions });

        var errorBody = new StackPanel { Spacing = 6 };
        errorBody.Children.Add(new TextBlock { Text = "需要注意", FontSize = 15, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Amber });
        errorBody.Children.Add(error);
        errorPanel.Child = errorBody;
        page.Children.Add(errorPanel);
        page.Children.Add(new TextBlock
        {
            Text = "SCM 状态可能遗漏短暂的中间变化，也没有最大刷新延迟保证。暂停观察只释放 Axiom 的订阅，不会暂停或停止 Windows 服务。",
            Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap
        });

        return new ScrollViewer
        {
            Background = Brush(246, 248, 251),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = page
        };
    }

    private static Border Card() => new()
    {
        Background = Brush(255, 255, 255), BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18), Padding = new Thickness(22)
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 13, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = Muted
    };

    private static StackPanel Detail(string label, TextBlock value)
    {
        value.Foreground = Ink;
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(Label(label));
        row.Children.Add(value);
        return row;
    }

    private static void SetActionStyle(Button button, SolidColorBrush background, SolidColorBrush foreground)
    {
        button.Background = background;
        button.Foreground = foreground;
        button.Padding = new Thickness(18, 10, 18, 10);
        button.CornerRadius = new CornerRadius(9);
        button.MinHeight = 44;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(new Color { A = 255, R = r, G = g, B = b });

    private async Task StartAsync()
    {
        if (busy || closed) return;
        busy = true;
        mode = ConnectionMode.Connecting;
        connectionError = null;
        ApplyState(null);
        try
        {
            if (pipe is null || !pipe.IsConnected)
            {
                readCancel?.Cancel(); pipe?.Dispose();
                var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("No user SID.");
                pipe = new NamedPipeClientStream(".", ServiceStatusProtocol.PipeName(sid), PipeDirection.InOut, PipeOptions.Asynchronous);
                readCancel = new CancellationTokenSource();
                await pipe.ConnectForUserAsync(3000, readCancel.Token);
                _ = ReadLoopAsync(pipe, readCancel.Token);
            }
            await SendAsync("observe");
        }
        catch (Exception exception) { ShowConnectionError(exception.Message); }
        finally { busy = false; UpdateButtons(); }
    }

    private async Task PauseAsync()
    {
        if (busy || mode != ConnectionMode.Observing) return;
        busy = true;
        UpdateButtons();
        try { await SendAsync("pause"); }
        catch (Exception exception) { ShowConnectionError(exception.Message); }
        finally { busy = false; UpdateButtons(); }
    }

    private async Task RetryAsync()
    {
        if (busy) return;
        if (mode == ConnectionMode.Disconnected) { await StartAsync(); return; }
        if (mode != ConnectionMode.Observing) return;
        busy = true;
        mode = ConnectionMode.Connecting;
        ApplyState(null);
        try { await SendAsync("retry"); }
        catch (Exception exception) { ShowConnectionError(exception.Message); }
        finally { busy = false; UpdateButtons(); }
    }

    private async Task SendAsync(string type)
    {
        await writes.WaitAsync();
        try
        {
            var message = new ServiceMessage(1, 0, 1, type, Guid.NewGuid().ToString("N"),
                ServiceStatusProtocol.Scope, ServiceStatusProtocol.ServiceName, DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10));
            await ServiceStatusProtocol.WriteAsync(pipe!, message, CancellationToken.None);
        }
        finally { writes.Release(); }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ServiceStatusProtocol.ReadAsync(stream, cancellationToken);
                if (message is null) break;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(pipe, stream) || closed) return;
                    if (message.Type == "error") ShowConnectionError(message.Error ?? "请求失败。");
                    else if (message.Type is "snapshot" or "status")
                    {
                        mode = message.Observing ? ConnectionMode.Observing : ConnectionMode.Paused;
                        connectionError = null;
                        ApplyState(message);
                    }
                });
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(pipe, stream) && !closed) ShowConnectionError("宿主连接已断开。请选择“重新连接 / 重试”。");
        });
    }

    private void ApplyState(ServiceMessage? message)
    {
        var known = mode == ConnectionMode.Observing && message?.State is not null;
        state.Text = known ? Translate(message!.State) : "未知";
        AutomationProperties.SetName(state, "Windows Event Log 服务状态：" + state.Text);
        state.Foreground = known ? StateBrush(message!.State) : Gray;
        stateDescription.Text = mode switch
        {
            ConnectionMode.Connecting => "正在连接本地宿主，等待新的服务状态样本。",
            ConnectionMode.Paused => "Axiom 已暂停观察；Windows 服务本身没有被暂停。",
            ConnectionMode.Disconnected => "与本地宿主断开，当前服务状态不可用。",
            _ when !known => "宿主正在观察，但当前没有可确认的服务状态。",
            _ => Describe(message!.State)
        };
        stateDescription.Foreground = Muted;
        connection.Text = mode switch
        {
            ConnectionMode.Connecting => "正在连接 / 等待样本",
            ConnectionMode.Observing => "正在观察",
            ConnectionMode.Paused => "已暂停观察（服务未暂停）",
            _ => "连接断开"
        };
        lastQuery.Text = mode switch
        {
            ConnectionMode.Paused => "暂停前记录：" + FormatTime(message?.LastSuccessAt),
            ConnectionMode.Observing when !known => "此前成功查询：" + FormatTime(message?.LastSuccessAt),
            ConnectionMode.Observing => FormatTime(message?.LastSuccessAt),
            _ => "当前无可用查询时间"
        };
        statusBasis.Text = mode == ConnectionMode.Observing
            ? "Windows 服务控制管理器（SCM）最近报告的状态；经 Axiom Engine 传递。"
            : "当前没有可用状态；Axiom 不推断服务已停止。";
        var detail = connectionError ?? message?.Error;
        error.Text = detail ?? "";
        error.Foreground = Amber;
        errorPanel.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        retry.Content = mode == ConnectionMode.Disconnected ? "重新连接" : "重新建立观察";
        UpdateButtons();
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "无";

    private static string Translate(string? value) => value switch
    {
        "running" => "运行中", "stopped" => "已停止", "start-pending" => "正在启动",
        "stop-pending" => "正在停止", "continue-pending" => "正在继续",
        "pause-pending" => "正在暂停", "paused" => "服务已暂停", _ => "未知"
    };

    private static string Describe(string? value) => value switch
    {
        "running" => "SCM 最近报告此服务正在运行。此状态不代表应用健康检查结果。",
        "stopped" => "SCM 最近报告此服务已停止。",
        "paused" => "SCM 最近报告 Windows 服务自身已暂停。",
        "start-pending" => "SCM 最近报告服务正在启动。",
        "stop-pending" => "SCM 最近报告服务正在停止。",
        "continue-pending" => "SCM 最近报告服务正在继续。",
        "pause-pending" => "SCM 最近报告服务正在暂停。",
        _ => "当前服务状态未知。"
    };

    private static SolidColorBrush StateBrush(string? value) => value switch
    {
        "running" => Green,
        "stopped" => Gray,
        _ => Amber
    };

    private void ShowConnectionError(string detail)
    {
        readCancel?.Cancel();
        pipe?.Dispose();
        pipe = null;
        mode = ConnectionMode.Disconnected;
        connectionError = detail;
        ApplyState(null);
    }

    private void UpdateButtons()
    {
        start.Visibility = mode == ConnectionMode.Paused ? Visibility.Visible : Visibility.Collapsed;
        pause.Visibility = mode == ConnectionMode.Observing ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = mode is ConnectionMode.Observing or ConnectionMode.Disconnected ? Visibility.Visible : Visibility.Collapsed;
        start.IsEnabled = !busy;
        pause.IsEnabled = !busy;
        retry.IsEnabled = !busy;
    }
}
