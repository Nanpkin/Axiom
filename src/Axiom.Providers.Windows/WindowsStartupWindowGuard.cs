using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsStartupWindowGuard
{
    internal static ProgramControlResult Start(string path, string arguments, string directory, int seconds, CancellationToken token)
    {
        if (seconds is < 1 or > 20) return new(false, "启动保护时长必须为 1–20 整数秒。");
        token.ThrowIfCancellationRequested();
        // Native launch state and target hooks are per process; unrelated programs
        // must not queue behind another program's entire startup protection period.
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var reopening = new EventWaitHandle(false, EventResetMode.ManualReset);
                var pid = 0;
                var observingSince = DateTimeOffset.UtcNow;
                var observer = new WindowsGameProcessObserver([path], change =>
                {
                    if (change.Running && change.Instance is { } instance && instance.StartedAtUtc >= observingSince &&
                        Volatile.Read(ref pid) is var launched && launched != 0 && instance.ProcessId != launched) reopening.Set();
                });
                uint error;
                GuardReport report;
                try
                {
                    observer.Start();
                    error = StartGuarded(path, arguments, directory, ComponentPath(), checked((uint)seconds * 1000), token.WaitHandle.SafeWaitHandle, reopening.SafeWaitHandle, out pid, out report);
                }
                finally { observer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                if (error == 1223 && token.IsCancellationRequested) throw new OperationCanceledException(token);
                return error == 0
                    ? new(true, report.Cloaked == 0
                        ? $"已启动 {Path.GetFileName(path)}（PID {pid}）；保护期已结束，本次未确认遮蔽到窗口。启动器另行拉起的程序不在保护范围。"
                        : $"已启动 {Path.GetFileName(path)}（PID {pid}）；本进程启动遮蔽已结束，之后手动打开窗口不再隐藏。")
                    : new(false, FailureReason(path, pid, error, report));
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return new(false, $"{Path.GetFileName(path)}：启动保护组件不可用，请更新完整 Axiom 程序；此选项仅支持 x64 桌面程序。");
            }
        }
    }

    internal static string FailureReason(string path, int pid, uint error, GuardReport report)
    {
        var stage = report.Stage switch { 1 => "准备启动", 2 => "创建进程", 3 => "初始化进程加载器",
            4 => "加载启动保护组件", 5 => "初始化启动保护组件", 6 => "等待启动窗口保护结束", 7 => "解除启动保护",
            8 => "确认保护结果", _ => "启动保护" };
        var code = report.LastFailure != 0 ? report.LastFailure : error;
        var detail = new Win32Exception((int)code).Message.TrimEnd('。', '.');
        var state = report.Resumed == 0 ? pid == 0 ? "未创建目标进程。" : "目标主线程尚未运行，本次创建的进程已结束。"
            : report.Alive != 0 ? "目标进程仍在运行，但尚未确认正常就绪。" : "目标进程已退出。";
        return $"{Path.GetFileName(path)}{(pid == 0 ? "" : $"（PID {pid}）")}：{stage}未完成：{detail}（{code}）。{state}";
    }

    private static string ComponentPath()
    {
        // A target can retain this DLL after protection ends. Load its verified copy from
        // a content-addressed temporary cache so it never locks the fixed Axiom preview.
        var source = Path.Combine(AppContext.BaseDirectory, "Axiom.StartupWindowGuard.dll");
        var bytes = File.ReadAllBytes(source);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(Path.GetTempPath(), "Axiom.StartupWindowGuard");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("启动保护缓存目录不能是重解析点。");
        var target = Path.Combine(directory, hash + ".dll");
        if (!File.Exists(target))
        {
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target)) { }
            }
            finally { File.Delete(temporary); }
        }
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(target)), SHA256.HashData(bytes)))
            throw new IOException("启动保护组件缓存校验失败。");
        foreach (var previous in Directory.EnumerateFiles(directory, "*.dll").Take(32))
        {
            if (string.Equals(previous, target, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(previous);
            if (name.Length != 64 || !name.All(Uri.IsHexDigit)) continue;
            try { File.Delete(previous); }
            catch (IOException) { } // Still loaded by another target; leave it in place.
            catch (UnauthorizedAccessException) { }
        }
        return target;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("Axiom.StartupWindowGuard.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint StartGuarded(string path, string arguments, string directory, string component, uint timeoutMs, SafeWaitHandle cancellation, SafeWaitHandle reopening, out int pid, out GuardReport report);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuardReport
    {
        public uint Size, TargetPid, TimeoutMs;
        public int Ready, Finished, Patched, Created, Cloaked, Failures, Reopen, HiddenSignal, HideCalls;
        public uint LastFailure;
        public uint Stage, Resumed, Alive;
    }
}
