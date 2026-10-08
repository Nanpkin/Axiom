using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class OpenTargetCapabilities
{
    public const string Open = "target.open";
    public static TaskCapability Descriptor { get; } = new(Open, 1, TaskCapabilityKind.Action, "日常工具", "打开目录或网页",
        "用资源管理器打开目录，或用默认浏览器打开 http/https 网页。",
        [new("kind", "打开什么", TaskFieldKind.Choice, true, "不接受脚本、程序、文件或自定义协议。", "folder", 16, [new("folder", "目录"), new("url", "网页")]),
         new("target", "目录路径或网址", TaskFieldKind.Text, true, "目录需本机完整路径；网页必须以 https:// 或 http:// 开头，不含账号密码。", null, 2048, []),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "等待由规则调度；暂停会取消尚未打开的请求。", "0", 8, [], 0, 86400)],
        "每次触发请求打开一次；暂停不关闭已经打开的目录或网页。成功表示系统接受打开请求，不代表网页加载完成。",
        "会显示窗口或标签页；频繁触发可能重复打开。只处理你明确配置的路径/网址，不执行任意命令。");
    public static (string Kind, string Target, double Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Open || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Keys.Any(k => !Descriptor.Fields.Any(f => f.Id == k))) throw new ArgumentException("打开目标配置无效。");
        var kind = invocation.Parameters.GetValueOrDefault("kind"); var target = invocation.Parameters.GetValueOrDefault("target") ?? "";
        if (target.Length is < 1 or > 2048 || target.Any(char.IsControl) || target != target.Trim()) throw new ArgumentException("请填写有效的目录或网址。");
        if (!double.TryParse(invocation.Parameters.GetValueOrDefault("delaySeconds"), NumberStyles.Number, CultureInfo.InvariantCulture, out var delay) || !double.IsFinite(delay) || delay is < 0 or > 86400) throw new ArgumentException("延迟超出范围。");
        if (kind == "folder")
        {
            if (!Path.IsPathFullyQualified(target) || target.StartsWith("\\\\", StringComparison.Ordinal) || target.IndexOfAny(['"', '*', '?', '<', '>', '|']) >= 0) throw new ArgumentException("请选择本机完整目录路径。");
            target = Path.GetFullPath(target);
        }
        else if (kind == "url")
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || target.Contains('\\')) throw new ArgumentException("网页仅支持不含账号密码的 http/https 网址。");
            target = uri.AbsoluteUri;
        }
        else throw new ArgumentException("请选择目录或网页。");
        return (kind, target, delay);
    }
}
