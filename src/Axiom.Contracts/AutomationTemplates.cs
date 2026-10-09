using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record AutomationTemplate(string Id, string Name, string Description, AutomationTask Draft);

// Recipes reference the live catalog; they do not duplicate capability schemas or save rules.
public static class AutomationTemplates
{
    private sealed record Recipe(string Id, string Name, string Description, string Detection, string Action,
        (string Field, string Value)[] DetectionValues, (string Field, string Value)[] ActionValues);
    private static readonly Recipe[] Recipes =
    [
        new("launch-companion", "打开程序后启动配套工具", "选择检测程序和要启动的工具；工具已运行时跳过，结束后不自动关闭。", "process.running", "program.start", [], []),
        new("cleanup-companion", "程序退出后关闭配套工具", "只请求正常关闭最近启动的一个匹配实例；不强制结束、不自动重开，未保存内容可能需要确认。", "process.exited", "program.close", [], [("mode", "graceful"), ("instances", "latest")]),
        new("window-cleanup", "工作窗口全部消失后收尾", "选择工作程序和配套工具；全部工作主窗口隐藏或销毁后请求正常关闭配套工具最新实例。最小化不触发，程序可能仍在后台；不强杀、不自动重开。", WindowPresenceCapabilities.Absent, "program.close", [], [("mode", "graceful"), ("instances", "latest")]),
        new("restore-work", "睡眠恢复后打开工作工具", "选择要启动的程序；恢复不等于已经解锁，已运行则跳过，不补发之前的唤醒。", SystemEventCapabilities.Event, "program.start", [("event", "resumed")], []),
        new("battery-power-saver", "切换到电池时使用节能方案", "切换到电池的新通知后，一次切换为本机标准节能方案；若本机没有该方案会失败，不创建。之后保留节能方案，接电不自动恢复；请审阅后启用。", SystemEventCapabilities.Event, PowerSchemeCapabilities.Set, [("event", "battery")], [("scheme", "saver")]),
        new("open-work-folder", "解锁后打开工作目录", "填写本机目录路径；每次新解锁请求打开一次，之后不会自动关闭目录窗口。", SystemEventCapabilities.Event, OpenTargetCapabilities.Open, [("event", "unlocked")], [("kind", "folder")]),
        new("wifi-work-folder", "连接工作 Wi-Fi 后打开目录", "手工填写本机保存的 Wi-Fi 配置文件名和工作目录；任一所选配置新连接后打开一次，同组选项间切换不重复打开。首次已连接不补发，暂停不关闭已打开的目录。", WifiProfileCapabilities.Connected, OpenTargetCapabilities.Open, [], [("kind", "folder")]),
        new("wifi-leave-lock", "离开指定 Wi-Fi 后请求锁屏", "手工填写本机保存的 Wi-Fi 配置文件名；全部确认断开后请求锁定当前会话，需要正常登录解锁。未知不当断开、首次已断开不执行，不检查网络可信性，不自动解锁。", WifiProfileCapabilities.Disconnected, SessionLockCapabilities.Lock, [], []),
        new("usb-dock-tools", "接入指定 USB 设备后启动工具", "从设备管理器复制提供 USB_DEVICE 接口的设备完整实例路径，并选择配套程序；新接入时启动一次，其他所选设备仍存在时不重复启动。首次已接入不补发，移除不自动关闭工具。", UsbDeviceCapabilities.Present, "program.start", [], []),
        new("window-prepare", "窗口出现后整理", "选择检测和目标程序，再选择布局；初始已有窗口不补执行，多窗口时只处理一个匹配窗口。", WindowPresenceCapabilities.Visible, WindowCapabilities.State, [], [("state", "maximize")]),
        new("focus-layout", "切到应用时摆到左半屏", "选择前台应用与目标程序；按触发时的显示器工作区整理一次，之后手动调整不会被持续干预。", ForegroundCapabilities.Active, WindowCapabilities.State, [], [("state", "left")]),
        new("display-layout", "显示变化后整理窗口", "选择目标程序和布局；显示模式通知后整理一次，按该窗口当前显示器处理，不识别特定屏幕组合、不恢复旧布局。", SystemEventCapabilities.Event, WindowCapabilities.State, [("event", "display-changed")], [("state", "left")]),
        new("shortcut-next-monitor", "按快捷键把窗口移到下一个显示器", "选择快捷键和目标程序完整路径；按执行时显示器桌面位置循环移屏并居中80%，最大化/最小化窗口先还原。只有一屏时跳过，不抢回后续手动调整。", HotkeyCapabilities.Pressed, WindowMonitorCapabilities.Move, [], [("monitor", "next")]),
        new("display-primary-window", "显示变化后把窗口放回主显示器", "选择目标程序完整路径；显示模式通知后移到执行时主显示器并居中80%，最大化/最小化窗口先还原。不匹配特定设备、不恢复旧坐标，也不持续固定。", SystemEventCapabilities.Event, WindowMonitorCapabilities.Move, [("event", "display-changed")], [("monitor", "primary")]),
        new("workday-reminder", "仅工作时段定期提醒", "默认工作日9–18点，每30分钟请求安静提醒；填写内容后审阅启用。结束时刻不触发，无节假日判断，不补发错过机会。", WindowedIntervalCapabilities.Due, NotificationCapabilities.Show, [], []),
        new("desktop-reminder", "定期显示桌面提醒", "填写提醒内容；默认每30分钟请求一次安静提醒。Windows勿扰可能隐藏通知，不适合紧急提醒。", IntervalCapabilities.Elapsed, NotificationCapabilities.Show, [], []),
        new("pdf-arrival", "文件夹出现PDF后打开工具", "填写本机文件夹和配套程序；新建或重命名出现PDF后一秒合并触发，不读取内容、不证明下载完成、不传递文件名。可给动作增加延迟。", FileArrivalCapabilities.Arrived, "program.start", [("kind", "pdf")], []),
        new("microphone-shortcut", "按快捷键静音默认通信麦克风", "选择快捷键；只静音Windows默认通信麦克风一次，不自动取消静音。应用选择的其他输入设备可能仍收音，请核对应用。", HotkeyCapabilities.Pressed, MicrophoneCapabilities.Set, [], [("operation", "mute")]),
        new("lock-mute", "锁屏后静音默认播放设备", "锁屏时静音一次，不停止播放、不改麦克风，解锁不自动恢复。之后可手动取消静音；请审阅后启用。", SystemEventCapabilities.Event, AudioPlaybackCapabilities.Set, [("event", "locked")], [("operation", "mute")]),
        new("focus-quiet-apps", "切到工作应用时降低播放应用音量", "选择前台工作程序及实际发声程序完整路径；只把默认播放设备上的活动会话音量设为30%，不取消静音、不自动恢复。目标未播放或无会话会说明失败。", ForegroundCapabilities.Active, ApplicationAudioCapabilities.Set, [], [("operation", "volume"), ("percent", "30")]),
        new("keep-transfer-awake", "传输或会议程序运行期间保持系统唤醒", "选择需要持续运行的程序；任意一个运行时持有Axiom自己的电源请求，全部退出或状态未知后释放。默认允许屏幕关闭，不改变电源计划、不阻止手动睡眠。", "process.running", KeepAwakeCapabilities.Hold, [], [("target", "system")]),
        new("once-meeting", "指定日期打开会议页面", "填写日期、本地时刻和http/https会议网址；只等待未来时刻，不补发已错过的会议，不自动加入或操作会议。", OnceTimeCapabilities.Due, OpenTargetCapabilities.Open, [], [("kind", "url")]),
        new("daily-document", "工作日定时打开计划文档", "手工填写本机文档完整路径；默认工作日9点交给默认关联应用打开，不读取内容、不保证加载完成，暂停不关闭已打开文档。", DailyTimeCapabilities.Due, OpenTargetCapabilities.Open, [], [("kind", "document")]),
        new("daily-work-folder", "工作日定时打开目录", "填写工作目录，选择本地时刻和星期；默认周一至周五9点。只等未来时刻，错过不补发，也不唤醒电脑。", DailyTimeCapabilities.Due, OpenTargetCapabilities.Open, [], [("kind", "folder")]),
        new("regular-reminder", "定期打开提醒页", "填写提醒页的 http/https 网址和间隔；首次也需等待完整间隔，每次到期会请求打开一次，请避免过短间隔造成重复标签页。", IntervalCapabilities.Elapsed, OpenTargetCapabilities.Open, [], [("kind", "url")]),
        new("pin-shortcut", "按快捷键置顶工具窗口", "选择全局快捷键与目标程序；一次置顶。可把窗口状态改为取消置顶建立另一条规则。", HotkeyCapabilities.Pressed, WindowCapabilities.State, [], [("state", "topmost")])
    ];
    public static AutomationTemplate[] Build(AutomationSnapshot catalog)
    {
        var result = new List<AutomationTemplate>();
        foreach (var recipe in Recipes)
        {
            if (!catalog.Combinations.Contains(new(recipe.Detection, recipe.Action))) continue;
            var detection = Invocation(recipe.Detection, TaskCapabilityKind.Detection, recipe.DetectionValues);
            var action = Invocation(recipe.Action, TaskCapabilityKind.Action, recipe.ActionValues);
            if (detection is null || action is null) continue;
            result.Add(new(recipe.Id, recipe.Name, recipe.Description, new(Guid.NewGuid().ToString("D"), 0, recipe.Name, true, detection, action)));
        }
        return [.. result];

        TaskInvocation? Invocation(string id, TaskCapabilityKind kind, (string Field, string Value)[] overrides)
        {
            var capability = catalog.Capabilities.SingleOrDefault(c => c.Id == id && c.Kind == kind);
            if (capability is null) return null;
            var values = capability.Fields.Where(f => f.DefaultValue is not null || f.Kind == TaskFieldKind.Boolean)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue ?? "false").ToBuilder();
            foreach (var (fieldId, value) in overrides)
            {
                var field = capability.Fields.SingleOrDefault(f => f.Id == fieldId);
                if (field is null || value.Length > field.MaximumLength || field.Kind == TaskFieldKind.Choice && !field.Choices.Any(c => c.Value == value)) return null;
                values[fieldId] = value;
            }
            return new(id, capability.SchemaVersion, values.ToImmutable());
        }
    }
}
