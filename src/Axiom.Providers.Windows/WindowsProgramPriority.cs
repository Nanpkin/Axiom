using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsProgramPriority
{
    private static readonly BoundedNativeCommand Commands = new("程序优先级", TimeSpan.FromSeconds(5));
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> SetAsync(IReadOnlyCollection<string> paths, string priority, CancellationToken token)
    {
        if (paths.Count is < 1 or > 32 || paths.Any(p => !Path.IsPathFullyQualified(p) || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("程序优先级路径无效。");
        _ = ProgramPriorityOperation.Class(priority);
        var targets = paths.ToArray();
        return Commands.RunAsync(cancellation => Apply(targets, priority, cancellation), token);
    }

    private static (bool Success, string Reason) Apply(string[] paths, string priority, CancellationToken token)
    {
        var candidates = Capture(paths, token);
        try { return ProgramPriorityOperation.Apply(candidates, paths, priority, token); }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }
    private static List<Candidate> Capture(string[] paths, CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        using var current = Process.GetCurrentProcess();
        var candidates = new List<Candidate>();
        try
        {
            // One snapshot, initialized and pinned before the first write. No waiting for future instances.
            var ids = new HashSet<int>();
            foreach (var name in paths.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var processes = Process.GetProcessesByName(name!);
                try { foreach (var process in processes) ids.Add(process.Id); }
                finally { foreach (var process in processes) process.Dispose(); }
                if (ids.Count > 4096) throw new IOException("同名进程数量超出安全处理范围，本次未修改。");
            }
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                candidates.Add(new Candidate((uint)id, sid, current.SessionId, token));
            }
            return candidates;
        }
        catch { foreach (var candidate in candidates) candidate.Dispose(); throw; }
    }

    private sealed class Candidate : IPriorityProcess, IDisposable
    {
        private readonly SafeProcessHandle handle;
        private readonly uint pid;
        private readonly string sid;
        private readonly int session;
        private readonly long creation;
        private readonly string? path;
        private readonly PriorityProcessIdentity identity;
        private readonly int openError;
        private readonly CancellationToken token;

        internal Candidate(uint pid, string sid, int session, CancellationToken token)
        {
            this.pid = pid; this.sid = sid; this.session = session; this.token = token;
            // SYNCHRONIZE | QUERY_LIMITED_INFORMATION | SET_INFORMATION. This handle owns all later operations.
            handle = OpenProcess(0x00101200, false, pid);
            if (handle.IsInvalid) { openError = Marshal.GetLastWin32Error(); identity = openError == 87 ? PriorityProcessIdentity.Missing : PriorityProcessIdentity.Unknown; return; }
            try
            {
                if (WaitForSingleObject(handle, 0) == 0) { identity = PriorityProcessIdentity.Missing; return; }
                if (!ProcessIdToSessionId(pid, out var actualSession)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (actualSession != session || WindowsProcessIdentity.ReadOwnerSid(handle.DangerousGetHandle()) != sid)
                { identity = PriorityProcessIdentity.Other; return; }
                path = WindowsProcessIdentity.ReadImagePath(handle.DangerousGetHandle());
                if (!GetProcessTimes(handle, out creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                identity = PriorityProcessIdentity.Owned;
            }
            catch (Win32Exception error) { openError = error.NativeErrorCode; identity = PriorityProcessIdentity.Unknown; }
            catch (UnauthorizedAccessException) { openError = 5; identity = PriorityProcessIdentity.Unknown; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { identity = PriorityProcessIdentity.Unknown; }
        }

        public PriorityProcessIdentity Identify(out string? value)
        {
            value = path;
            if (openError == 5) throw new Win32Exception(5);
            if (identity != PriorityProcessIdentity.Owned) return identity;
            if (WaitForSingleObject(handle, 0) != 258 || !GetProcessTimes(handle, out var created, out _, out _, out _) || creation != created)
                return PriorityProcessIdentity.Unknown;
            if (!ProcessIdToSessionId(pid, out var actualSession) || actualSession != session ||
                WindowsProcessIdentity.ReadOwnerSid(handle.DangerousGetHandle()) != sid ||
                !string.Equals(WindowsProcessIdentity.ReadImagePath(handle.DangerousGetHandle()), path, StringComparison.OrdinalIgnoreCase))
                return PriorityProcessIdentity.Unknown;
            return PriorityProcessIdentity.Owned;
        }
        public uint GetPriority()
        {
            var value = GetPriorityClass(handle);
            if (value == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return value;
        }
        public void SetPriority(uint value) => SetPriority(value, token);
        internal void SetPriority(uint value, CancellationToken cancellation)
        {
            if (Identify(out _) != PriorityProcessIdentity.Owned) throw new InvalidOperationException("进程身份已变化。");
            cancellation.ThrowIfCancellationRequested();
            if (!SetPriorityClass(handle, value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetPriorityClass(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetPriorityClass(SafeProcessHandle process, uint priority);
}
