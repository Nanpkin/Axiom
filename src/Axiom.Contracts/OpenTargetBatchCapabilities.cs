using System.Collections.Immutable;
using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class OpenTargetBatchCapabilities
{
    public const string Open = "open-target.batch";
    public const int MaximumItems = 8;
    public static TaskCapability Descriptor { get; } = new(Open, 1, TaskCapabilityKind.Action, "日常工具", "打开一组工作目录、文档和网页",
        "按填写顺序一次打开最多8个本机目录、文档或http/https网页。",
        [new("targets", "工作目标列表", TaskFieldKind.TextList, true,
            "每行一项，最多8项，每项含类型最多255字符。格式例如：目录 | C:\\Work；文档 | C:\\Work\\Plan.pdf；网页 | https://example.com。按列表顺序打开，不能填程序、脚本、快捷方式或含宏文档。", null, 2048, [], EditAsList: true),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "暂停取消尚未执行的整组请求。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "全部先检查类型、路径和可访问性，再按顺序请求打开；整批共用8秒期限和现有打开动作的原生准入，不排队。失败、取消或超时停止后续项，说明已确认受理的数量；正在发送的一项可能已受理但未确认。成功只代表Windows受理，不保证内容载入。",
        "仅用户手工填写本机路径和http/https网址，不支持网络共享、映射网络驱动器、备用数据流或自定义协议。可能显示多个窗口/标签页；不检查文档内容和默认关联，不传入参数。不会关闭此前打开的窗口、不回滚、不自动重试。关闭Axiom窗口后后台仍执行。");
    public static ((string Kind, string Target)[] Targets, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Open || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("targets", out var text) || string.IsNullOrWhiteSpace(text) || text.Length > 2048 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请填写1–8个工作目标和0–86400整数延迟。");
        var lines = text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
        if (lines.Length is < 1 or > MaximumItems || lines.Any(line => line.Length is < 1 or > 255 || line.Any(char.IsControl)))
            throw new ArgumentException("每行填写一个工作目标，最多8项，每项最多255字符，不能包含空行或控制字符。");
        var result = new List<(string Kind, string Target)>();
        foreach (var line in lines)
        {
            var split = line.IndexOf('|');
            if (split < 0) throw new ArgumentException("每项须填写“目录 | 完整路径”、“文档 | 完整路径”或“网页 | http/https网址”。");
            var kind = line[..split].Trim() switch { "目录" => "folder", "文档" => "document", "网页" => "url", _ => throw new ArgumentException("请选择目录、文档或网页类型。") };
            var target = line[(split + 1)..].Trim();
            var validated = OpenTargetCapabilities.Validate(new(OpenTargetCapabilities.Open, 1,
                ImmutableDictionary<string, string>.Empty.Add("kind", kind).Add("target", target).Add("delaySeconds", "0")));
            result.Add((validated.Kind, validated.Target));
        }
        return ([.. result], d);
    }
}
