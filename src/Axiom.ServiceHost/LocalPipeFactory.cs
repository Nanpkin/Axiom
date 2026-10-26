using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Axiom.ServiceHost;

internal static class LocalPipeFactory
{
    internal const int AutomationMaximumClients = 8;
    internal const uint AutomationMaximumInstances = AutomationMaximumClients + 1;
    // Explicitly rejects remote clients. The DACL grants only SYSTEM and the launch user's SID.
    internal static NamedPipeServerStream Create(string name, string sid, uint maximumInstances = 8)
    {
        if (maximumInstances is < 1 or > 254) throw new ArgumentOutOfRangeException(nameof(maximumInstances));
        // Elevated tokens default ownership to Administrators. Pin the launch user's
        // ownership so LocalUserPipe can check the actual user at both elevation levels.
        var sddl = $"O:{sid}D:P(A;;GA;;;SY)(A;;GA;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new InvalidOperationException($"Pipe ACL failed: Win32 {Marshal.GetLastWin32Error()}");
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor
            };
            var handle = CreateNamedPipe("\\\\.\\pipe\\" + name,
                0x00000003 | 0x40000000, // PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED
                0x00000008, // byte mode, wait mode, PIPE_REJECT_REMOTE_CLIENTS
                maximumInstances, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"CreateNamedPipeW: Win32 {error}");
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
