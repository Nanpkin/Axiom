using System.Text.Json;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private sealed record GameModePreset(bool Manual = false, bool Automatic = false, string Processes = "");
    private GameModeSnapshot? gameMode;
    private GameModeSettings? gameModeDraft;
    private string? gameModeError;
    private bool gameModeStateConfirmed;
    private static string GameModePresetPath => Path.Combine(AxiomUserData.Root, "game-mode-preset.json");
    private bool GameModeDirty => gameModeDraft is { } draft && (gameMode is null ||
        draft.ManualEnabled != gameMode.Settings.ManualEnabled || draft.AutomaticEnabled != gameMode.Settings.AutomaticEnabled ||
        !(draft.ProcessNames ?? []).SequenceEqual(gameMode.Settings.ProcessNames ?? [], StringComparer.OrdinalIgnoreCase));

    private async Task ReadGameModeAsync(bool replaceDraft = false)
    {
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "read-game-mode", Guid.NewGuid().ToString("N")));
            if (response.Type != "game-mode" || response.GameMode is null) throw new InvalidDataException(response.Error);
            var dirty = GameModeDirty;
            gameMode = response.GameMode; gameModeStateConfirmed = true; gameModeError = null;
            if (replaceDraft || !dirty || !GameModeDirty) gameModeDraft = gameMode.Settings;
        }
        catch (Exception) { gameModeStateConfirmed = false; gameModeError = "游戏模式状态未确认。输入和上次读取的配置已保留，请刷新重试。"; }
    }

    private async Task OpenGameModeAsync()
    {
        if (busy || closed) return;
        SetBusy(true);
        try { await ReadGameModeAsync(); Render(); }
        finally { SetBusy(false); }
    }

    private void BuildGameModeSettings()
    {
        var saved = gameModeDraft ?? gameMode?.Settings ?? new GameModeSettings();
        var body = Column(0);
        var state = !gameModeStateConfirmed ? "状态未确认" : gameMode?.Active switch { true => "读取时已开启", false => "读取时已关闭", _ => "状态未知" };
        var statusRow = new Grid { ColumnSpacing = 10 };
        statusRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var statusText = Text("●  " + state, 13, !gameModeStateConfirmed || gameMode?.Active is null ? Amber : gameMode.Active == true ? Accent : Muted);
        statusText.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(statusText, gameModeStateConfirmed ? gameMode!.Reason + " 刷新查看最新状态。" : "请连接后台并刷新。");
        statusRow.Children.Add(statusText);
        var wiki = new HyperlinkButton { Content = "使用说明", Foreground = Accent, Padding = new Thickness(0), MinHeight = 28 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(wiki, "游戏模式说明（Wiki）");
        wiki.Click += (_, _) => OpenDetail("游戏模式 Wiki");
        Grid.SetColumn(wiki, 1); statusRow.Children.Add(wiki); content.Children.Add(statusRow);
        var manual = new ToggleSwitch { IsOn = saved.ManualEnabled, HorizontalAlignment = HorizontalAlignment.Right,
            OnContent = "", OffContent = "", MinWidth = 0 };
        var automatic = new ToggleSwitch { IsOn = saved.AutomaticEnabled, HorizontalAlignment = HorizontalAlignment.Right,
            OnContent = "", OffContent = "", MinWidth = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(manual, "手动开启游戏模式");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(automatic, "按程序自动开启游戏模式");
        Grid SettingRow(string glyph, string title, string description, ToggleSwitch toggle)
        {
            var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, Foreground = Muted });
            var labels = Column(3); labels.Children.Add(Text(title, 14, Ink, true)); labels.Children.Add(Text(description, 12, Muted));
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            toggle.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
            return row;
        }
        body.Children.Add(SettingRow("\uE7FC", "手动开启", "关闭后仍按自动条件决定", manual));
        body.Children.Add(new Border { Height = 1, Background = Line });
        body.Children.Add(SettingRow("\uE713", "自动开启", "任一所选程序运行时开启", automatic));
        var names = string.Join("\n", saved.ProcessNames ?? []);
        var message = Text(gameModeError ?? (GameModeDirty ? "更改尚未保存" : gameMode is null ? "等待后台确认配置" : "配置已保存"), 12, gameModeError is null ? Muted : Amber);
        void Changed()
        {
            gameModeDraft = saved with { ManualEnabled = manual.IsOn, AutomaticEnabled = automatic.IsOn, ProcessNames = ProcessGroups.SplitNames(names) };
            message.Text = GameModeDirty ? "更改尚未保存" : gameMode is null ? "等待后台确认配置" : "配置已保存";
        }
        var targetCount = Text($"程序名单 · {ProcessGroups.SplitNames(names).Length} 个", 13, Ink);
        var picker = BuildProcessPicker(names, value =>
        {
            names = value; Changed();
            targetCount.Text = $"程序名单 · {ProcessGroups.SplitNames(names).Length} 个";
        });
        var targets = new Expander { Header = targetCount, Content = picker,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(targets);
        manual.Toggled += (_, _) => Changed();
        automatic.Toggled += (_, _) => { Changed(); if (automatic.IsOn) targets.IsExpanded = true; };
        content.Children.Add(Card(body, padding: 12));
        var actions = Row(8);
        var save = Action("保存", true); save.IsEnabled = gameMode is not null;
        save.Click += async (_, _) =>
        {
            if (busy || gameMode is null) return;
            Changed();
            if (automatic.IsOn && string.IsNullOrWhiteSpace(names)) { message.Text = "请先选择至少一个自动开启的程序。"; return; }
            SetBusy(true);
            try
            {
                var response = await WorkbenchClient.RequestAsync(new(1, "save-game-mode", Guid.NewGuid().ToString("N"), GameModeSettings: gameModeDraft));
                if (response.Type != "game-mode" || response.GameMode is null)
                { gameModeStateConfirmed = false; gameModeError = "保存未确认，输入已保留。" + (response.Error ?? "请刷新核对后台配置。"); }
                else
                {
                    gameMode = response.GameMode; gameModeDraft = gameMode.Settings; gameModeStateConfirmed = true; gameModeError = null;
                    ShowNotice("游戏模式配置已保存。", true);
                }
            }
            catch (Exception) { gameModeStateConfirmed = false; gameModeError = "暂未收到保存结果，输入已保留。请刷新核对后台配置。"; }
            finally { SetBusy(false); Render(); }
        };
        actions.Children.Add(save);
        var reload = new MenuFlyoutItem { Text = "重读已保存配置" };
        reload.Click += async (_, _) =>
        {
            if (busy || GameModeDirty && !await ConfirmAsync("重读已保存配置？", "这会放弃本页尚未保存的输入。", "重读配置")) return;
            SetBusy(true);
            try { await ReadGameModeAsync(true); Render(); }
            finally { SetBusy(false); }
        };
        var menu = new MenuFlyout(); menu.Items.Add(reload);
        if (File.Exists(GameModePresetPath))
        {
            var import = new MenuFlyoutItem { Text = "导入原预设" };
            import.Click += async (_, _) =>
            {
                if (busy || GameModeDirty && !await ConfirmAsync("导入原预设？", "这会替换本页输入；仍需显式保存后才生效。", "导入")) return;
                try
                {
                    var preset = JsonSerializer.Deserialize<GameModePreset>(File.ReadAllText(GameModePresetPath)) ?? throw new JsonException();
                    var selection = new ProcessSelectionState(null); selection.Set(ProcessGroups.SplitNames(preset.Processes), true);
                    gameModeDraft = saved with { ManualEnabled = preset.Manual, AutomaticEnabled = preset.Automatic, ProcessNames = ProcessGroups.SplitNames(selection.Value) };
                    gameModeError = "原预设已填入，尚未生效。请核对并保存。"; Render();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { message.Text = "无法导入原预设：" + error.Message; }
            };
            menu.Items.Add(import);
        }
        var more = Action("更多"); more.Click += (_, _) => menu.ShowAt(more); actions.Children.Add(more);
        var footerRow = new Grid { ColumnSpacing = 16 };
        footerRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footerRow.Children.Add(actions); message.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(message, 1); footerRow.Children.Add(message); content.Children.Add(footerRow);
    }
}
