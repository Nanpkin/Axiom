using System.Diagnostics;
using System.Xml.Linq;

namespace Axiom.Launcher;

internal static class LoginTaskCleanup
{
    internal static bool Owns(string xml, string launcher)
    {
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var actions = document.Root?.Element(ns + "Actions")?.Elements().ToArray();
        if (actions is not { Length: 1 } || actions[0].Name != ns + "Exec") return false;
        var command = actions[0].Element(ns + "Command")?.Value;
        return !string.IsNullOrWhiteSpace(command) && Path.IsPathFullyQualified(command) &&
            Path.GetFullPath(command).Equals(Path.GetFullPath(launcher), StringComparison.OrdinalIgnoreCase) &&
            actions[0].Element(ns + "Arguments")?.Value == "--background";
    }
    internal static void RemoveIfOwned(string launcher, string sid)
    {
        var name = "Axiom-" + sid;
        var query = Run("/Query", "/TN", name, "/XML");
        if (query.ExitCode != 0)
        {
            var all = Run("/Query", "/FO", "CSV", "/NH");
            if (all.ExitCode != 0 || all.Output.Split('\n').Any(line => line.StartsWith("\"\\" + name + "\",", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("无法核对登录启动登记，请在 Axiom 设置中关闭登录启动后重试。");
            return;
        }
        if (!Owns(query.Output, launcher)) return; // Preserve a task pointing to another installation.
        if (Run("/Delete", "/TN", name, "/F").ExitCode != 0) throw new IOException("无法移除登录启动登记。");
    }
    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法读取登录启动登记。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("登录启动登记读取超时。"); }
        error.GetAwaiter().GetResult();
        return (process.ExitCode, output.GetAwaiter().GetResult());
    }
}
