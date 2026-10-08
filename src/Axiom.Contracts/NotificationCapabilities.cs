using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class NotificationCapabilities
{
    public const string Show = "notification.show";
    public static TaskCapability Descriptor { get; } = new(Show, 1, TaskCapabilityKind.Action, "提醒", "显示桌面提醒",
        "通过Windows通知区域请求显示一条文字提醒，不打开浏览器。",
        [new("title", "提醒标题", TaskFieldKind.Text, true, "最多63个字符，不含控制字符。", "Axiom 提醒", 63, []),
         new("body", "提醒内容", TaskFieldKind.Text, true, "最多255个字符，可换行；只显示文字，不打开链接或执行内容。", null, 255, []),
         new("sound", "请求系统提示音", TaskFieldKind.Boolean, true, "默认关闭；实际通知行为由Windows设置决定。", "false", 5, []),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "由规则调度，暂停取消尚未开始的提醒。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "一次只处理一条提醒，不排队补发；临时托盘图标最多保留约10秒，通知显示时长由Windows决定。UI退出仍可触发，点击提醒不执行其他动作。",
        "系统勿扰、锁屏或通知设置可能隐藏提醒。成功仅表示Windows接受请求，不证明已显示或阅读；不会等待用户确认，不是可靠的紧急提醒渠道。暂停可取消未发出的请求，不能保证撤回已经显示的通知。");
    public static (string Title, string Body, bool Sound, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Show || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 4 ||
            !invocation.Parameters.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title) || title.Length > 63 || title.Any(char.IsControl) ||
            !invocation.Parameters.TryGetValue("body", out var body) || string.IsNullOrWhiteSpace(body) || body.Length > 255 || body.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ||
            !invocation.Parameters.TryGetValue("sound", out var sound) || sound is not ("true" or "false") ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请填写有效的提醒标题、内容、提示音选项和整数延迟。");
        return (title, body, sound == "true", d);
    }
}
