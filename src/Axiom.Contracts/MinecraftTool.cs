using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class MinecraftTool
{
    public const string Name = "Minecraft 图形设置";
    public const string TaskId = "d85904d6-8198-4cab-b3e3-6ba5802cf56d";
    public const string Launch = "minecraft.launcher-start";
    public const string Apply = "minecraft.prefer-native";
    public const string DefaultLauncher = "Plain Craft Launcher 2.exe";
    public static TaskCapability Detection { get; } = new(Launch, 1, TaskCapabilityKind.Detection, "程序", "PCL 启动",
        "监听启动器的新进程。", [new("launcher", "启动器文件名", TaskFieldKind.Text, true, "填写实际 .exe 文件名。", DefaultLauncher, 132, [])],
        "每个匹配的新进程触发一次，连接时不补发。", "");
    public static TaskCapability Action { get; } = new(Apply, 1, TaskCapabilityKind.Action, "显示", "Java 图形设置优先本机",
        "检查 javaw.exe 的 NVIDIA Vulkan/OpenGL 呈现方法。", [new("javaPath", "Java 路径", TaskFieldKind.FilePath, false, "可指定驱动中登记的 javaw.exe 完整路径。", null, 1024, [], BrowseLocalPath: true)], "需要时更新程序配置并回读。", "暂停保留已写入的设置。");

    public static string ValidateJavaPath(string value)
    {
        var path = ExecutablePaths.ValidatePath(value);
        if (!Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 javaw.exe。");
        return path;
    }

    public static AutomationTask Configure(string launcher, bool paused, long version, string? javaPath = null)
    {
        if (!ProcessGroups.ValidProcessName(launcher)) throw new ArgumentException("请填写启动器的 .exe 文件名。");
        return new(TaskId, version, Name, paused, new(Launch, 1, ImmutableDictionary<string,string>.Empty.Add("launcher", launcher)),
            new(Apply, 1, string.IsNullOrWhiteSpace(javaPath) ? ImmutableDictionary<string,string>.Empty :
                ImmutableDictionary<string,string>.Empty.Add("javaPath", ValidateJavaPath(javaPath))));
    }
}
