using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Axiom.Contracts;

namespace Axiom.Launcher;

internal static class Program
{
    private const uint MbIconError = 0x10;
    private const uint MbOk = 0;
    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string message, string title, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int informationClass, out int elevation, int length, out int required);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [STAThread]
    private static int Main(string[] args)
    {
        string? expectedSid = null;
        if (args.Length >= 2 && args[^2] == "--elevated-user")
        {
            expectedSid = args[^1]; args = args[..^2];
        }
        var background = args.Length == 1 && args[0] == "--background";
        var maintenance = args is ["--check-stopped"] or ["--prepare-uninstall"];
        try
        {
            if (args.Length != 0 && !background && !maintenance) throw new ArgumentException("不支持的启动参数。");
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var currentSid = identity.User?.Value ?? throw new UnauthorizedAccessException("无法确认当前用户。");
                if (expectedSid is not null && expectedSid != currentSid)
                    throw new UnauthorizedAccessException("请使用当前账户提升权限，不能切换到另一账户启动 Axiom。");
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    var elevated = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = true, Verb = "runas", Arguments = (args.Length == 1 ? args[0] + " " : "") + "--elevated-user " + currentSid,
                        WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var child = Process.Start(elevated);
                    if (maintenance && child is not null) { child.WaitForExit(); return child.ExitCode; }
                    return child is null ? 1 : 0;
                }
            }
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("无法确认当前用户。");
            if (maintenance)
            {
                foreach (var name in new[] { "Axiom.ServiceHost", "Axiom.SessionAgent", "Axiom.WinUI" })
                    foreach (var process in Process.GetProcessesByName(name))
                        using (process) if (process.SessionId == Process.GetCurrentProcess().SessionId) return 3;
                if (args[0] == "--prepare-uninstall") LoginTaskCleanup.RemoveIfOwned(Path.Combine(AppContext.BaseDirectory, "Axiom.exe"), sid);
                return 0;
            }
            using var launch = new Mutex(false, @"Local\Axiom.Launcher." + sid);
            var acquired = false;
            try
            {
                try { acquired = launch.WaitOne(TimeSpan.FromSeconds(45)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new TimeoutException("另一启动请求尚未结束，请稍后重试。");
                StartAsync(background, sid).GetAwaiter().GetResult();
            }
            finally { if (acquired) launch.ReleaseMutex(); }
            return 0;
        }
        catch (Exception error)
        {
            if (maintenance) return 1;
            if (background)
            {
                try
                {
                    var directory = AxiomUserData.Root;
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "startup-error.txt"), $"{DateTimeOffset.Now:O}\n{error.Message}");
                }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            }
            else MessageBoxW(0, error.Message, "Axiom 无法启动", MbOk | MbIconError);
            return 1;
        }
    }

    private static async Task StartAsync(bool background, string sid)
    {
        var root = AppContext.BaseDirectory;
        var layout = AxiomInstallLayout.At(root);
        var host = layout.Host;
        var agent = layout.Agent;
        var ui = layout.Ui;
        foreach (var path in new[] { host, agent, ui })
            if (!File.Exists(path)) throw new FileNotFoundException("程序文件不完整，请保留整个 Axiom 文件夹。", path);

        var session = Process.GetCurrentProcess().SessionId;
        StartIfMissing("Axiom.ServiceHost", host, "--background", session);
        StartIfMissing("Axiom.SessionAgent", agent, "--run", session);
        var ready = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await IsReadyAsync(sid)) { ready = true; break; }
            await Task.Delay(250);
        }
        if (!ready)
        {
            // A restricted desktop can deny a client connection to the status
            // pipe even though the host process is alive. Keep startup usable;
            // the UI will show the unavailable connection state and can retry.
            using var runningHost = FindRunning("Axiom.ServiceHost", host, session);
            if (runningHost is null)
                throw new TimeoutException("后台没有在预期时间内准备好。请检查是否有另一份 Axiom 正在运行。");
        }
        if (background) return;

        using var existing = FindRunning("Axiom.WinUI", ui, session);
        if (existing is not null)
        {
            if (existing.MainWindowHandle != 0)
            {
                ShowWindow(existing.MainWindowHandle, SwRestore);
                SetForegroundWindow(existing.MainWindowHandle);
            }
            return;
        }
        Start(ui, null);
    }

    private static void StartIfMissing(string name, string path, string args, int session)
    {
        using var existing = FindRunning(name, path, session);
        if (existing is null) Start(path, args);
    }

    private static Process? FindRunning(string name, string path, int session)
    {
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (process.SessionId != session) { process.Dispose(); continue; }
                if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"另一份 {name} 正在运行。请先关闭旧预览，再打开当前 Axiom。");
                if (!OpenProcessToken(process.Handle, 8, out var token)) throw new System.ComponentModel.Win32Exception();
                try
                {
                    if (!GetTokenInformation(token, 20, out var elevation, 4, out _) || elevation == 0)
                        throw new InvalidOperationException($"正在运行的 {name} 尚未提权，请在设置中停止后台后重新打开 Axiom。");
                }
                finally { CloseHandle(token); }
                return process;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                process.Dispose();
                throw new InvalidOperationException($"无法核对正在运行的 {name}，为避免连接错误版本，Axiom 已停止启动。");
            }
        }
        return null;
    }

    private static void Start(string path, string? args)
    {
        var start = new ProcessStartInfo(path)
        {
            Arguments = args ?? "",
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(path)}。");
    }

    private static async Task<bool> IsReadyAsync(string sid)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var pipe = new NamedPipeClientStream(".", GameHdrProtocol.HostPipe(sid), PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectForUserAsync(500, timeout.Token);
            var requestId = Guid.NewGuid().ToString("N");
            await GameHdrProtocol.WriteAsync(pipe, new(1, "status", requestId,
                DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(2)), timeout.Token);
            var reply = await GameHdrProtocol.ReadAsync(pipe, timeout.Token);
            // The host can serve the UI while the session agent is reconnecting;
            // the status page will show that degraded state instead of making
            // ordinary startup fail outright.
            return reply?.Type == "status" && reply.RequestId == requestId && reply.Status is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or InvalidDataException)
        { return false; }
    }
}
