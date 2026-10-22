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

    private void BuildGameModeTool()
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
        body.Children.Add(Text("游戏模式", 18, Ink, true));
        body.Children.Add(Text("游戏期间减少后台监测的资源占用。", 14, Muted));
        body.Children.Add(Text("配置预览 · 尚未生效。当前只保存开启方式和程序名单，尚未接入自动检测、监测暂停或降频。", 13, Amber));
        var manual = new ToggleSwitch { Header = "手动开启（预设）", IsOn = saved.Manual,
            OnContent = "希望开启", OffContent = "希望关闭" };
        var automatic = new ToggleSwitch { Header = "指定程序运行时自动开启（预设）", IsOn = saved.Automatic,
            OnContent = "已选择", OffContent = "未选择" };
        body.Children.Add(manual); body.Children.Add(automatic);
        body.Children.Add(Text("后续接入后：手动开启会保持游戏模式；关闭手动开启后，按自动检测结果决定。任一所选程序运行即自动开启，全部退出后自动结束。", 12, Muted));
        var names = saved.Processes;
        var picker = BuildProcessPicker(names, value => names = value);
        var targets = new Expander { Header = "自动开启的程序", Content = picker,
            IsExpanded = saved.Automatic, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(targets);
        automatic.Toggled += (_, _) => { if (automatic.IsOn) targets.IsExpanded = true; };
        var message = Text(gameModePresetError ?? "修改后请点击保存；切页会放弃未保存输入。预设不会改变现有任务。", 12, Muted);
        var save = Action("保存游戏模式预设", true);
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
