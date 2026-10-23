using System.Globalization;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private BrightnessWheelSnapshot? brightnessWheel;
    private string? brightnessWheelError;
    private bool brightnessWheelUnconfirmed;

    private void BuildBrightnessWheelTool()
    {
        var body = Column(12); var heading = ToolHeading();
        heading.Children.Add(new Border { Width = 40, Height = 40, Background = Soft, CornerRadius = new CornerRadius(10),
            Child = new FontIcon { Glyph = "\uE706", FontSize = 20, Foreground = Accent } });
        var headingText = Text(BrightnessWheelTool.Name, 18, Ink, true); headingText.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(headingText, 1); heading.Children.Add(headingText); body.Children.Add(heading);
        body.Children.Add(Text("按住自选组合键并滚动鼠标滚轮，调整支持的显示器亮度。", 14, Muted));
        var toggle = new ToggleSwitch { Header = "启用亮度滚轮", OnContent = "已启用", OffContent = "已关闭",
            IsOn = brightnessWheel?.Settings.Enabled == true, IsEnabled = reachable && brightnessWheel is not null && !brightnessWheelUnconfirmed };
        toggle.Toggled += async (_, _) =>
        {
            if (brightnessWheel is null || busy) return;
            await MutateBrightnessWheelAsync(new(1, "save-brightness-wheel", Guid.NewGuid().ToString("N"), WheelSettings: brightnessWheel.Settings with { Enabled = toggle.IsOn }));
        };
        PlaceToolToggle(heading, toggle);
        if (brightnessWheel is { } wheel)
        {
            body.Children.Add(Text($"{BrightnessWheelTool.Describe(wheel.Settings)} · {(wheel.Settings.Target == "all" ? "全部支持的显示器" : "主显示器")} · 每格 {wheel.Settings.Step} 个百分点", 13, Muted));
            body.Children.Add(Text(wheel.State, 13, wheel.ManualTakenOver ? Amber : Muted));
            body.Children.Add(Text($"监听：{(wheel.Listening ? "正在监听组合键与滚轮" : "未在监听")} · 调整：{(wheel.Running ? "正在调整" : "当前未在调整")} · 自动亮度：{(wheel.ManualTakenOver ? "已由手动接管" : "未被此工具接管")}", 12, Muted));
            if (wheel.LastResult is { Length: > 0 } last) body.Children.Add(Text("上次结果：" + last, 12, Muted));
        }
        else body.Children.Add(Text("尚未读取亮度滚轮状态，请刷新后重试。", 13, Muted));
        if (brightnessWheelError is not null) body.Children.Add(Text(brightnessWheelError, 12, Amber));
        var actions = FlowActions(); var settings = Action("工具设置"); settings.IsEnabled = reachable;
        settings.Click += async (_, _) => await OpenBrightnessWheelSettingsAsync(); actions.Children.Add(settings);
        var resume = Action("恢复自动亮度"); resume.IsEnabled = reachable && brightnessWheel?.ManualTakenOver == true && !brightnessWheelUnconfirmed;
        resume.Click += async (_, _) =>
        {
            if (brightnessWheel is not null) await MutateBrightnessWheelAsync(new(1, "resume-brightness-wheel", Guid.NewGuid().ToString("N"), ExpectedVersion: brightnessWheel.Settings.Version));
        };
        actions.Children.Add(resume);
        var refresh = Action("刷新结果"); refresh.IsEnabled = reachable;
        refresh.Click += async (_, _) => { if (busy) return; SetBusy(true); try { await ReadBrightnessWheelAsync(); } finally { SetBusy(false); Render(); } };
        actions.Children.Add(refresh); body.Children.Add(actions);
        body.Children.Add(Help(brightnessWheel?.Settings.CoordinateAutomatic != false
            ? "滚轮修正当前降亮配置，不暂停自动检测。画面退出条件后恢复降亮前的原值；保留期内再次进入相同配置时沿用手动偏好。普通画面手动调节保留当前亮度，不强制100%。关闭窗口仍可使用。"
            : "滚轮接管后暂停自动亮度；关闭工具或点击恢复自动亮度后解除。"));
        content.Children.Add(Card(body));
    }

    private async Task<bool> ReadBrightnessWheelAsync()
    {
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "read-brightness-wheel", Guid.NewGuid().ToString("N")));
            if (response.Type != "brightness-wheel" || response.Wheel is null) throw new InvalidOperationException(response.Error ?? "亮度滚轮状态未确认。");
            brightnessWheel = response.Wheel; brightnessWheelError = null; brightnessWheelUnconfirmed = false; return true;
        }
        catch (Exception error) { brightnessWheelError = "无法读取亮度滚轮：" + error.Message; return false; }
    }

    private async Task MutateBrightnessWheelAsync(AutomationMessage request)
    {
        if (busy || closed || brightnessWheelUnconfirmed) return;
        SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(request);
            if (response.Type != "brightness-wheel" || response.Wheel is null)
            {
                if (response.Error is null) brightnessWheelUnconfirmed = true;
                brightnessWheelError = response.Error ?? "更新结果未确认，请刷新核对。";
                ShowNotice(brightnessWheelError); return;
            }
            brightnessWheel = response.Wheel; brightnessWheelError = null;
            ShowNotice(request.Type == "resume-brightness-wheel" ? "后台已确认恢复请求，请查看当前状态。" : "后台已确认设置，请查看监听与执行状态。", true);
        }
        catch (Exception) { brightnessWheelUnconfirmed = true; brightnessWheelError = "更新结果未确认，请刷新状态后再操作。"; ShowNotice(brightnessWheelError); }
        finally { SetBusy(false); Render(); }
    }

    private async Task OpenBrightnessWheelSettingsAsync()
    {
        if (busy || closed || activeDialog is not null) return;
        SetBusy(true); bool read;
        try { read = await ReadBrightnessWheelAsync(); } finally { SetBusy(false); }
        if (!read || brightnessWheel is null || closed) { ShowNotice(brightnessWheelError ?? "亮度滚轮状态未确认。"); Render(); return; }
        var original = brightnessWheel.Settings;
        var enabled = new ToggleSwitch { Header = "启用亮度滚轮", OnContent = "已启用", OffContent = "已关闭", IsOn = original.Enabled };
        CheckBox Modifier(string label, ShortcutModifiers flag) => new() { Content = label, IsChecked = original.Modifiers.HasFlag(flag) };
        var ctrl = Modifier("Ctrl", ShortcutModifiers.Control); var alt = Modifier("Alt", ShortcutModifiers.Alt);
        var shift = Modifier("Shift", ShortcutModifiers.Shift); var win = Modifier("Win", ShortcutModifiers.Windows);
        var modifiers = Row(14); modifiers.Children.Add(ctrl); modifiers.Children.Add(alt); modifiers.Children.Add(shift); modifiers.Children.Add(win);
        var key = new ComboBox { Header = "主键（可选）", ItemsSource = new[] { "无主键" }.Concat(HotkeyCapabilities.Keys).ToArray(),
            SelectedItem = original.Key.Length == 0 ? "无主键" : original.Key, HorizontalAlignment = HorizontalAlignment.Stretch };
        var target = new ComboBox { Header = "调整范围", ItemsSource = new[] { "主显示器", "全部支持的显示器" },
            SelectedIndex = original.Target == "all" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        TextBox Number(string label, int value)
        {
            var field = new TextBox { Header = label, Text = value.ToString(CultureInfo.InvariantCulture), MaxLength = 8 };
            AutomationProperties.SetName(field, label); return field;
        }
        var step = Number("每格调整（百分点，1–20）", original.Step);
        var smooth = Number("平滑时长（毫秒，50–1000）", original.SmoothMilliseconds);
        var writes = Number("写入请求上限（次/秒，1–60）", original.WritesPerSecond);
        var coordinate = new ToggleSwitch { Header = "与自动亮度协调", IsOn = original.CoordinateAutomatic, OnContent = "修正场景，检测继续", OffContent = "手动接管，暂停自动" };
        var memory = Number("相同降亮配置的偏好保留（分钟，0–240）", original.PreferenceMinutes);
        var inputMode = new ComboBox { Header = "滚轮输入方式", ItemsSource = new[] { "即时响应（鼠标滚轮）", "累计标准刻度（高精度滚轮 / 触控板）" },
            SelectedIndex = original.ImmediateResponse ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var body = Column(12); body.MaxWidth = 620;
        body.Children.Add(Help("仅调整支持 DDC/CI 的硬件显示器，请在显示器菜单中开启 DDC/CI。主显示器不支持时不会自动改用其他屏幕。"));
        body.Children.Add(enabled); body.Children.Add(Text("按住的修饰键（至少选择一项）", 14, Ink, true)); body.Children.Add(modifiers); body.Children.Add(key);
        body.Children.Add(Help("可选 Ctrl + Alt + 滚轮，或 Ctrl + Shift + B + 滚轮等组合。向上调亮，向下调暗。"));
        body.Children.Add(target); body.Children.Add(inputMode);
        body.Children.Add(Help("即时响应会让每条非零滚轮输入至少调整一次，不等攒满标准刻度；连续输入过于敏感时，改用累计标准刻度。大幅输入仍合并为相应步数。"));
        body.Children.Add(step); body.Children.Add(smooth); body.Children.Add(writes);
        body.Children.Add(Help("写入次数是后台亮度请求的上限，不是面板帧率；实际变化受显示器响应与亮度粒度影响。连续滚动由后台合并。"));
        body.Children.Add(coordinate); body.Children.Add(memory);
        body.Children.Add(Help("建议开启协调、保留30分钟：例如原100%自动降至50%，手动调至65%后本轮保持65%，退出仍恢复原100%。同配置和原基准的后续降亮可沿用65%；时长仅在下一轮检查，不会读着同一页面突然重置。0表示仅当前一轮。偏好不跨后台重启；不同降亮参数或原基准不共用。旧式接管模式仍可选。"));
        var error = Text("", 12, Amber); body.Children.Add(error);
        var editorPanel = new ContentControl { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var resultUnknown = false;
        while (!closed)
        {
            var reload = false;
            var dialog = new ContentDialog { Title = BrightnessWheelTool.Name + "设置", Content = new ScrollViewer { Content = editorPanel, MaxHeight = Math.Clamp(shell.ActualHeight - 200, 240, 680) },
                PrimaryButtonText = "保存亮度滚轮设置", SecondaryButtonText = "重新读取已保存设置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = !resultUnknown, XamlRoot = shell.XamlRoot };
            dialog.Resources["ContentDialogMaxWidth"] = Math.Min(760d, shell.XamlRoot.Size.Width - 48);
            dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
            dialog.SecondaryButtonClick += (_, _) => reload = true;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true; var deferral = args.GetDeferral();
                try
                {
                    static int Parse(TextBox field, string label) => int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
                        ? value : throw new ArgumentException(label + "须填写整数。");
                    var flags = (ctrl.IsChecked == true ? ShortcutModifiers.Control : 0) | (alt.IsChecked == true ? ShortcutModifiers.Alt : 0) |
                        (shift.IsChecked == true ? ShortcutModifiers.Shift : 0) | (win.IsChecked == true ? ShortcutModifiers.Windows : 0);
                    if (key.SelectedItem is not string selectedKey || target.SelectedIndex < 0 || inputMode.SelectedIndex < 0) throw new ArgumentException("请选择主键、调整范围和滚轮输入方式。");
                    var proposed = new BrightnessWheelSettings(original.Version, enabled.IsOn, flags, selectedKey == "无主键" ? "" : selectedKey,
                        target.SelectedIndex == 1 ? "all" : "primary", Parse(step, "每格调整"), Parse(smooth, "平滑时长"), Parse(writes, "写入请求上限"), inputMode.SelectedIndex == 0,
                        coordinate.IsOn, Parse(memory, "偏好保留时长"));
                    BrightnessWheelTool.Validate(proposed);
                    SetBusy(true); editorPanel.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false; dialog.IsSecondaryButtonEnabled = false; dialog.CloseButtonText = "请稍候";
                    var response = await WorkbenchClient.RequestAsync(new(1, "save-brightness-wheel", Guid.NewGuid().ToString("N"), WheelSettings: proposed));
                    if (response.Type != "brightness-wheel" || response.Wheel is null)
                    {
                        if (response.Error is null) resultUnknown = brightnessWheelUnconfirmed = true;
                        error.Text = response.Error ?? "保存结果未确认，草稿已保留，请重新读取核对。"; return;
                    }
                    brightnessWheel = response.Wheel; brightnessWheelError = null; brightnessWheelUnconfirmed = false; args.Cancel = false;
                    ShowNotice("后台已确认保存亮度滚轮设置，请查看实际监听和执行状态。", true);
                }
                catch (ArgumentException problem) { error.Text = problem.Message; }
                catch (Exception) { resultUnknown = brightnessWheelUnconfirmed = true; error.Text = "暂未收到保存结果。草稿已保留，请取消后刷新核对，或显式重新读取已保存设置。"; }
                finally { editorPanel.IsEnabled = true; dialog.IsPrimaryButtonEnabled = !resultUnknown; dialog.IsSecondaryButtonEnabled = true; dialog.CloseButtonText = "取消"; SetBusy(false); deferral.Complete(); }
            };
            await ShowDialogAsync(dialog);
            // Detach the persistent controls before showing this draft again.
            if (dialog.Content is ScrollViewer scroll) scroll.Content = null;
            if (!reload) break;
            if (!await ConfirmAsync("重新读取并放弃当前输入？", "将读取后台已保存的版本，当前窗口内未保存的修改会丢弃。", "重新读取")) continue;
            SetBusy(true); try { read = await ReadBrightnessWheelAsync(); } finally { SetBusy(false); }
            if (!read) { error.Text = brightnessWheelError ?? "读取失败，当前输入已保留。"; continue; }
            if (brightnessWheel is not null)
            {
                original = brightnessWheel.Settings; enabled.IsOn = original.Enabled;
                inputMode.SelectedIndex = original.ImmediateResponse ? 0 : 1;
                coordinate.IsOn = original.CoordinateAutomatic; memory.Text = original.PreferenceMinutes.ToString(CultureInfo.InvariantCulture);
                ctrl.IsChecked = original.Modifiers.HasFlag(ShortcutModifiers.Control); alt.IsChecked = original.Modifiers.HasFlag(ShortcutModifiers.Alt);
                shift.IsChecked = original.Modifiers.HasFlag(ShortcutModifiers.Shift); win.IsChecked = original.Modifiers.HasFlag(ShortcutModifiers.Windows);
                key.SelectedItem = original.Key.Length == 0 ? "无主键" : original.Key; target.SelectedIndex = original.Target == "all" ? 1 : 0;
                step.Text = original.Step.ToString(CultureInfo.InvariantCulture); smooth.Text = original.SmoothMilliseconds.ToString(CultureInfo.InvariantCulture);
                writes.Text = original.WritesPerSecond.ToString(CultureInfo.InvariantCulture); error.Text = "已重新读取后台保存的设置。"; resultUnknown = false;
            }
        }
        Render();
    }
}
