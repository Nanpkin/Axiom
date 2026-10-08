using System.Security.Cryptography;
using System.Text;

namespace Axiom.Contracts;

// Shared path-list rules for executable actions and their generic selection field.
public static class ExecutablePaths
{
    public const int MaximumEntries = 32;
    public const int MaximumTextLength = 32768;
    public static string[] Parse(string? value)
    {
        if (value is null || value.Length > MaximumTextLength) throw new ArgumentException("程序路径名单过长。");
        var paths = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ValidatePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length is < 1 or > MaximumEntries) throw new ArgumentException("请选择 1–32 个目标程序。");
        return paths;
    }
    public static string ValidatePath(string value)
    {
        var path = value.Trim();
        if (path.Length > 1024 || path.IndexOfAny(['\r', '\n', '"', '*', '?', '<', '>', '|']) >= 0 ||
            !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请填写本机程序的完整 .exe 路径，每行一个；不接受脚本或网络路径。");
        path = Path.GetFullPath(path);
        var forbidden = new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe",
            "Axiom.exe", "Axiom.ServiceHost.exe", "Axiom.SessionAgent.exe", "Axiom.WinUI.exe" };
        if (forbidden.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("此能力不支持命令解释器、脚本宿主或 Axiom 自身。");
        return path;
    }
    public static string ResourceId(IReadOnlyCollection<string> paths) => paths.Count == 1 ? paths.Single().ToLowerInvariant() :
        "targets:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", paths.Select(p => p.ToLowerInvariant()).Order(StringComparer.Ordinal)))));
}
