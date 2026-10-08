using Axiom.Domain;

namespace Axiom.Contracts;

public static class FileArrivalCapabilities
{
    public const string Arrived = "file.arrived";
    public static TaskCapability Descriptor { get; } = new(Arrived, 1, TaskCapabilityKind.Detection, "文件夹", "指定文件夹出现新文件",
        "监听指定本机文件夹中的新建或重命名文件，不读取文件内容。",
        [new("directory", "本机文件夹完整路径", TaskFieldKind.DirectoryPath, true, "最多512字符，不支持网络路径；不包含子文件夹。", null, 512, []),
         new("kind", "文件类别", TaskFieldKind.Choice, true, "按扩展名分类，不读取内容或判断下载完成。", "pdf", 12,
            [new("pdf", "PDF"), new("office", "Office文档"), new("image", "常见图片"), new("archive", "常见压缩文件"), new("any", "所有文件")])],
        "已有文件不补发；文件创建或重命名进入选定类别后，将首个事件起一秒内的变化合并为一次。相同目录共享通知，每次检测最多16种目录/类别组合；无目录扫描、无递归或内容读取。UI退出规则继续。",
        "文件出现不代表写入或下载完成，建议给动作适当延迟。重命名也可能触发，无法传递文件名给动作。监听绑定启动时目录对象，移动目录后请修正路径并重新启用。目录不可用/通知溢出时相关文件检测标未知，修复后暂停并重新启用；不扫描补历史。");
    public static (string Directory, string Kind) Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Arrived || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 2 ||
            !invocation.Parameters.TryGetValue("directory", out var directory) || !invocation.Parameters.TryGetValue("kind", out var kind)) throw new ArgumentException("文件到达检测配置无效。");
        return Validate(directory, kind);
    }
    private static (string Directory, string Kind) Validate(string directory, string kind)
    {
        if (kind is not ("pdf" or "office" or "image" or "archive" or "any")) throw new ArgumentException("请选择有效的文件类别。");
        if (directory.Length is < 1 or > 512 || directory != directory.Trim() || directory.Any(char.IsControl) || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith("\\\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal) || directory.IndexOfAny(['"', '*', '?', '<', '>', '|']) >= 0)
            throw new ArgumentException("请选择有效的本机文件夹完整路径，不支持网络路径。");
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), kind);
    }
    public static string Resource((string Directory, string Kind) value) => value.Kind + ":" + value.Directory;
    public static (string Directory, string Kind) ParseResource(string resource)
    {
        var separator = resource.IndexOf(':');
        if (separator < 1) throw new ArgumentException("文件到达检测资源无效。");
        var result = Validate(resource[(separator + 1)..], resource[..separator]);
        if (Resource(result) != resource) throw new ArgumentException("文件到达检测资源格式无效。");
        return result;
    }
    public static string Label(string resource)
    {
        var spec = ParseResource(resource);
        return spec.Directory + " · " + Descriptor.Fields[1].Choices.Single(c => c.Value == spec.Kind).Label;
    }
}
