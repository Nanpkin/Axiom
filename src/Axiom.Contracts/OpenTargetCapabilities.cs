using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class OpenTargetCapabilities
{
    public const string Open = "target.open";
    private static readonly string[] DocumentExtensions = [".pdf", ".txt", ".md", ".rtf", ".csv", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];
    public static TaskCapability Descriptor { get; } = new(Open, 1, TaskCapabilityKind.Action, "日常工具", "打开目录、文档或网页",
        "用资源管理器打开目录、默认关联应用打开本机文档，或默认浏览器打开http/https网页。",
        [new("kind", "打开什么", TaskFieldKind.Choice, true, "文档仅支持列出的常见格式，不接受脚本、程序、快捷方式或自定义协议。", "folder", 16, [new("folder", "目录"), new("document", "本机文档或图片"), new("url", "网页")]),
         new("target", "目录/文档路径或网址", TaskFieldKind.Text, true, "本机完整路径；文档支持pdf/txt/md/rtf/csv/docx/xlsx/pptx/odt/ods/odp/jpg/jpeg/png/bmp/gif/webp。网页只支持无账号密码的http/https。", null, 2048, [], BrowseLocalPath: true),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "等待由规则调度；暂停会取消尚未打开的请求。", "0", 8, [], 0, 86400)],
        "每次触发请求打开一次，同一会话最多一个原生打开请求，不排队；8秒未返回则报告结果未确认，继续占用准入直到调用真正结束，不重试。取消可能已打开窗口；文档交给Windows默认关联应用，不传入参数、不读取内容。暂停不关闭已打开的窗口；成功仅表示系统接受请求，不代表应用已载入文档或网页。",
        "会显示窗口或标签页；频繁触发可能重复打开。文档应来自可信来源，默认关联由Windows决定；此动作不检查文档内容。只处理明确配置的本机路径/网址，不执行任意命令，不支持网络共享。");
    public static (string Kind, string Target, double Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Open || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Keys.Any(k => !Descriptor.Fields.Any(f => f.Id == k))) throw new ArgumentException("打开目标配置无效。");
        var kind = invocation.Parameters.GetValueOrDefault("kind"); var target = invocation.Parameters.GetValueOrDefault("target") ?? "";
        if (target.Length is < 1 or > 2048 || target.Any(char.IsControl) || target != target.Trim()) throw new ArgumentException("请填写有效的目录、文档或网址。");
        if (!double.TryParse(invocation.Parameters.GetValueOrDefault("delaySeconds"), NumberStyles.Number, CultureInfo.InvariantCulture, out var delay) || !double.IsFinite(delay) || delay is < 0 or > 86400) throw new ArgumentException("延迟超出范围。");
        if (kind is "folder" or "document")
        {
            if (!Path.IsPathFullyQualified(target) || target.StartsWith("\\\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal) || target.IndexOf(':', target.Length > 1 && target[1] == ':' ? 2 : 0) >= 0 || target.IndexOfAny(['"', '*', '?', '<', '>', '|']) >= 0) throw new ArgumentException("请选择本机完整目录或文档路径，不支持网络共享、设备路径或数据流。");
            target = Path.GetFullPath(target);
            if (kind == "document" && !DocumentExtensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("文档格式不受支持；不能打开程序、脚本、快捷方式或含宏Office格式。");
        }
        else if (kind == "url")
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || target.Contains('\\')) throw new ArgumentException("网页仅支持不含账号密码的 http/https 网址。");
            target = uri.AbsoluteUri;
        }
        else throw new ArgumentException("请选择目录、文档或网页。");
        return (kind, target, delay);
    }
}
