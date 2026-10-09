using System.Collections.Immutable;
using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class WorkPreparationCapabilities
{
    public const string Prepare = "work.prepare";
    public static TaskCapability Descriptor { get; } = new(Prepare, 1, TaskCapabilityKind.Action, "日常工具", "按顺序准备工作工具和资料",
        "一个快捷键按手填顺序启动工具、打开目录、文档和网页，最多8步。",
        [new("steps", "工作准备步骤", TaskFieldKind.TextList, true,
            "每行一步，最多8步，每行最多255字符：程序 | 完整.exe路径；目录 | 完整路径；文档 | 完整路径；网页 | http/https网址。程序正常显示、无参数、使用所在目录，已运行跳过并继续。名单弹窗会合并完全相同行。", null, 2048, [], EditAsList: true),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "等待期间暂停取消整组请求。", "0", 5, [], Minimum: 0, Maximum: 86400)],
        "全部先检查类型和本机可访问性，再按顺序请求；整组8秒、与打开资料动作共用单准入，不排队。失败、取消或超时停止后续，回执说明完成/跳过数量及未确认步骤；已开始项可能已打开。成功仅表示受理或已运行跳过，不保证工具就绪或资料载入。",
        "仅手工本机完整exe路径、原打开动作支持的文档及无凭据http(s)，不支持脚本、快捷方式、宏文档、网络共享/映射驱动器、参数或窗口布局。不读取内容，不关闭/回滚/重试；暂停不撤销已发请求，关闭Axiom窗口后后台仍执行。");

    public static ((string Kind, string Target)[] Steps, int Delay) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Prepare || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("steps", out var text) || string.IsNullOrWhiteSpace(text) || text.Length > 2048 ||
            !invocation.Parameters.TryGetValue("delaySeconds", out var delay) || !int.TryParse(delay, NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d is < 0 or > 86400)
            throw new ArgumentException("请填写1–8步工作准备和0–86400整数延迟。");
        var lines = text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
        if (lines.Length is < 1 or > 8 || lines.Any(line => line.Length is < 1 or > 255 || line.Any(char.IsControl)))
            throw new ArgumentException("每行填写一步，最多8步，每步最多255字符，不能含空行或控制字符。");
        var result = new List<(string Kind, string Target)>();
        foreach (var line in lines)
        {
            var separator = line.IndexOf('|');
            if (separator < 0) throw new ArgumentException("每步须填写类型 | 完整路径或网址。");
            if (line[..separator].Trim() == "程序")
            {
                var target = line[(separator + 1)..].Trim();
                // Apply the existing local-path syntax restriction as well as the program-start policy.
                var parameters = ImmutableDictionary<string, string>.Empty.Add("kind", "folder").Add("target", target).Add("delaySeconds", "0");
                target = OpenTargetCapabilities.Validate(new(OpenTargetCapabilities.Open, 1, parameters)).Target;
                target = ProgramCapabilities.Validate(new("program.start", 1, ImmutableDictionary<string, string>.Empty.Add("executable", target).Add("delaySeconds", "0")));
                result.Add(("program", target));
            }
            else result.Add(OpenTargetBatchCapabilities.Validate(new(OpenTargetBatchCapabilities.Open, 1,
                ImmutableDictionary<string, string>.Empty.Add("targets", line).Add("delaySeconds", "0"))).Targets.Single());
        }
        return ([.. result], d);
    }
}
