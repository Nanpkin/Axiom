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
        new("open-work-folder", "解锁后打开工作目录", "填写本机目录路径；每次新解锁请求打开一次，之后不会自动关闭目录窗口。", SystemEventCapabilities.Event, OpenTargetCapabilities.Open, [("event", "unlocked")], [("kind", "folder")]),
        new("window-prepare", "窗口出现后整理", "选择检测和目标程序，再选择布局；初始已有窗口不补执行，多窗口时只处理一个匹配窗口。", WindowPresenceCapabilities.Visible, WindowCapabilities.State, [], [("state", "maximize")]),
        new("focus-layout", "切到应用时摆到左半屏", "选择前台应用与目标程序；按触发时的显示器工作区整理一次，之后手动调整不会被持续干预。", ForegroundCapabilities.Active, WindowCapabilities.State, [], [("state", "left")]),
        new("display-layout", "显示变化后整理窗口", "选择目标程序和布局；显示模式通知后整理一次，按该窗口当前显示器处理，不识别特定屏幕组合、不恢复旧布局。", SystemEventCapabilities.Event, WindowCapabilities.State, [("event", "display-changed")], [("state", "left")]),
        new("lock-mute", "锁屏后静音默认播放设备", "锁屏时静音一次，不停止播放、不改麦克风，解锁不自动恢复。之后可手动取消静音；请审阅后启用。", SystemEventCapabilities.Event, AudioPlaybackCapabilities.Set, [("event", "locked")], [("operation", "mute")]),
        new("once-meeting", "指定日期打开会议页面", "填写日期、本地时刻和http/https会议网址；只等待未来时刻，不补发已错过的会议，不自动加入或操作会议。", OnceTimeCapabilities.Due, OpenTargetCapabilities.Open, [], [("kind", "url")]),
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
