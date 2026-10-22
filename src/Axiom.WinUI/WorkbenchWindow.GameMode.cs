using System.Text.Json;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    // UI preset only. The host must own effective state when runtime support is added.
    private sealed record GameModePreset(bool Manual = false, bool Automatic = false, string Processes = "");
    private GameModePreset? gameModePreset;
    private string? gameModePresetError;
    private static string GameModePresetPath => Path.Combine(AxiomUserData.Root, "game-mode-preset.json");

    private void BuildGameModeSettings()
    {
        if (gameModePreset is null)
        {
            try
            {
                gameModePreset = File.Exists(GameModePresetPath)
                    ? JsonSerializer.Deserialize<GameModePreset>(File.ReadAllText(GameModePresetPath)) ?? new()
                    : new();
                var validated = new ProcessSelectionState(null);
                validated.Set(ProcessGroups.SplitNames(gameModePreset.Processes), true);
                gameModePreset = gameModePreset with { Processes = validated.Value };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { gameModePreset = new(); gameModePresetError = "无法读取游戏模式预设：" + error.Message; }
        }
        var saved = gameModePreset;
        var body = Column(12);
        var title = Column(5);
        title.Children.Add(Text("开启方式", 14, Ink, true));
        var badge = new Border { Background = PaleAmber, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            Child = Text("配置预览 · 尚未生效", 12, Amber) };
        body.Children.Add(SideBySide(title, badge));
        body.Children.Add(new Border { Height = 1, Background = Line });
        var manual = new ToggleSwitch { IsOn = saved.Manual, HorizontalAlignment = HorizontalAlignment.Right,
            OnContent = "希望开启", OffContent = "希望关闭" };
        var automatic = new ToggleSwitch { IsOn = saved.Automatic, HorizontalAlignment = HorizontalAlignment.Right,
            OnContent = "已选择", OffContent = "未选择" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(manual, "手动开启游戏模式预设");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(automatic, "按程序自动开启游戏模式预设");
        var manualTitle = Column(4); manualTitle.Children.Add(Text("手动开启", 14, Ink, true));
        manualTitle.Children.Add(Text("主动保持游戏模式。", 12, Muted));
        body.Children.Add(SideBySide(manualTitle, manual));
        var autoTitle = Column(4); autoTitle.Children.Add(Text("按程序自动开启", 14, Ink, true));
        autoTitle.Children.Add(Text("任一所选程序运行时开启，全部退出后结束。", 12, Muted));
        body.Children.Add(SideBySide(autoTitle, automatic));
        var names = saved.Processes;
        var targetCount = Text($"自动开启的程序 · 已选 {ProcessGroups.SplitNames(names).Length} 个", 13, Ink);
        var picker = BuildProcessPicker(names, value =>
        {
            names = value;
            targetCount.Text = $"自动开启的程序 · 已选 {ProcessGroups.SplitNames(names).Length} 个";
        });
        var targets = new Expander { Header = targetCount, Content = picker,
            IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(targets);
        automatic.Toggled += (_, _) => { if (automatic.IsOn) targets.IsExpanded = true; };
        body.Children.Add(Help("当前只保存开启方式和程序名单，自动检测及监测暂停、降频尚未接入。接入后手动开启优先保持；关闭手动开启后按自动检测决定。现有任务保持原有行为。", "游戏模式说明"));
        var message = Text(gameModePresetError ?? "修改后请保存；切页会放弃未保存输入。", 12, Muted);
        var save = Action("保存预设", true);
        save.Click += (_, _) =>
        {
            if (automatic.IsOn && string.IsNullOrWhiteSpace(names))
            { message.Text = "请先选择至少一个自动开启的程序。"; return; }
            var next = new GameModePreset(manual.IsOn, automatic.IsOn, names);
            var temporary = GameModePresetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(AxiomUserData.Root);
                File.WriteAllText(temporary, JsonSerializer.Serialize(next));
                File.Move(temporary, GameModePresetPath, overwrite: true);
                gameModePreset = next; gameModePresetError = null;
                message.Text = "预设已保存；游戏模式尚未生效，当前监测保持原有行为。";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { message.Text = "预设保存失败，请重试：" + error.Message; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        };
        body.Children.Add(save); body.Children.Add(message);
        content.Children.Add(Card(body));
    }
}
