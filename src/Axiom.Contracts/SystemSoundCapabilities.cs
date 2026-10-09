using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class SystemSoundCapabilities
{
    public const string Sound = "notification.sound";
    public static TaskCapability Descriptor { get; } = new(Sound, 1, TaskCapabilityKind.Action, "提醒", "播放一次系统提示音",
        "请求Windows播放一个内置系统声音，不弹出提醒窗口。",
        [new("sound", "声音", TaskFieldKind.Choice, true, "声音由Windows主题/系统声音设置决定，不能选择文件或网址。", "default", 7,
            [new("default", "默认系统提示音"), new("info", "信息提示音"), new("warning", "警告提示音"), new("error", "错误提示音")]),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未发送的请求。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "每次触发只请求一次，共用一个有界原生准入，不排队、不重试。UI退出后台仍可触发，不修改音量/静音、不播放文件、不联网、不录音或朗读文字。",
        "成功仅表示Windows接受声音请求，不保证听见；系统静音、声音主题、远程会话、设备及策略可能导致无声。不用于可靠的紧急报警。取消或超时不能撤回已接受的声音，暂停不停止已经开始的声音；不连续播放、不自动恢复。");
    public static (string Sound, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Sound || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("sound", out var sound) || sound is not ("default" or "info" or "warning" or "error") ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请选择内置系统声音和0–86400整数延迟；不支持文件、网址、循环或音量参数。");
        return (sound, d);
    }
}
