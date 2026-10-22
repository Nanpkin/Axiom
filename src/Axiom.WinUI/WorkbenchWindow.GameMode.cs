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
        var body = Column(12);
        var state = !gameModeStateConfirmed ? "状态未确认" : gameMode?.Active switch { true => "读取时已开启", false => "读取时已关闭", _ => "状态未知" };
        body.Children.Add(SideBySide(Text("开启方式", 14, Ink, true), Badge(state, !gameModeStateConfirmed || gameMode?.Active != true)));
        body.Children.Add(Text(gameModeStateConfirmed ? gameMode!.Reason + " 刷新可查看最新状态。" : "请连接后台并刷新，确认当前状态。", 12, Muted));
        body.Children.Add(new Border { Height = 1, Background = Line });
        var manual = new ToggleSwitch { IsOn = saved.ManualEnabled, HorizontalAlignment = HorizontalAlignment.Right,
            OnContent = "开启", OffContent = "关闭" };
        var automatic = new ToggleSwitch { IsOn = saved.AutomaticEnabled, HorizontalAlignment = HorizontalAlignment.Right,
            OnContent = "开启", OffContent = "关闭" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(manual, "手动开启游戏模式");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(automatic, "按程序自动开启游戏模式");
        var manualTitle = Column(4); manualTitle.Children.Add(Text("手动开启", 14, Ink, true));
        manualTitle.Children.Add(Text("保持开启；关闭后仍按自动检测决定。", 12, Muted));
        body.Children.Add(SideBySide(manualTitle, manual));
        var autoTitle = Column(4); autoTitle.Children.Add(Text("按程序自动开启", 14, Ink, true));
        autoTitle.Children.Add(Text("任一所选程序运行时开启，全部退出后结束。", 12, Muted));
        body.Children.Add(SideBySide(autoTitle, automatic));
        var names = string.Join("\n", saved.ProcessNames ?? []);
        var message = Text(gameModeError ?? (GameModeDirty ? "有未保存的更改。返回、切页和刷新会保留输入，保存后才生效。" : "修改后请保存，后台确认后生效。"), 12, Muted);
        void Changed()
        {
            gameModeDraft = saved with { ManualEnabled = manual.IsOn, AutomaticEnabled = automatic.IsOn, ProcessNames = ProcessGroups.SplitNames(names) };
            message.Text = GameModeDirty ? "有未保存的更改。返回、切页和刷新会保留输入，保存后才生效。" : "与已保存配置一致。";
        }
        var targetCount = Text($"自动开启的程序 · 已选 {ProcessGroups.SplitNames(names).Length} 个", 13, Ink);
        var picker = BuildProcessPicker(names, value =>
        {
            names = value; Changed();
            targetCount.Text = $"自动开启的程序 · 已选 {ProcessGroups.SplitNames(names).Length} 个";
        });
        var targets = new Expander { Header = targetCount, Content = picker,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(targets);
        manual.Toggled += (_, _) => Changed();
        automatic.Toggled += (_, _) => { Changed(); if (automatic.IsOn) targets.IsExpanded = true; };
        body.Children.Add(Help("在规则检测中选择游戏模式状态，通过逻辑与、逻辑非自行决定执行条件。普通条件组合不保证停止其他检测的采集；当前不会统一暂停任务或改变亮度。", "游戏模式与自动化规则"));
        var wiki = Action("游戏模式说明（Wiki）");
        wiki.Click += (_, _) => OpenDetail("游戏模式 Wiki");
        body.Children.Add(wiki);
        var actions = FlowActions();
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
        var reload = Action("重读已保存配置");
        reload.Click += async (_, _) =>
        {
            if (busy || GameModeDirty && !await ConfirmAsync("重读已保存配置？", "这会放弃本页尚未保存的输入。", "重读配置")) return;
            SetBusy(true);
            try { await ReadGameModeAsync(true); Render(); }
            finally { SetBusy(false); }
        };
        actions.Children.Add(reload);
        if (File.Exists(GameModePresetPath))
        {
            var import = Action("导入原预设");
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
            actions.Children.Add(import);
        }
        body.Children.Add(actions); body.Children.Add(message);
        content.Children.Add(Card(body));
    }
}
