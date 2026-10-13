using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Runtime.Versioning;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsPowerScheme
{
    private static readonly BoundedNativeCommand Commands = new("电源方案", TimeSpan.FromSeconds(5));
    public static Task DrainAsync() => Commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public static Task<(bool Success, string Reason)> SetAsync(Guid scheme, string name, CancellationToken token) =>
        Commands.RunAsync(cancellation => PowerSchemeOperation.Apply(new Api(), scheme, name, cancellation), token);

    public static Guid ReadActive(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var error = new Api().GetActive(out var scheme);
        if (error != 0) throw new Win32Exception((int)error, "无法读取当前电源方案。");
        if (scheme == Guid.Empty) throw new IOException("Windows返回无效电源方案。");
        token.ThrowIfCancellationRequested();
        return scheme;
    }

    private sealed class Api : IPowerSchemeApi
    {
        public uint Enumerate(uint index, out Guid scheme)
        {
            uint size = 16;
            var error = PowerEnumerate(0, 0, 0, 16, index, out scheme, ref size); // ACCESS_SCHEME
            return error == 0 && size != 16 ? 13u : error; // ERROR_INVALID_DATA
        }
        public uint SetActive(Guid scheme) => PowerSetActiveScheme(0, ref scheme);
        public uint GetActive(out Guid scheme)
        {
            scheme = default; var error = PowerGetActiveScheme(0, out var allocation);
            try
            {
                if (error != 0) return error;
                if (allocation == 0) return 13;
                scheme = Marshal.PtrToStructure<Guid>(allocation); return 0;
            }
            finally { if (allocation != 0) LocalFree(allocation); }
        }
    }
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(nint rootKey, nint scheme, nint subgroup, uint access, uint index, out Guid buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint rootKey, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint rootKey, out nint scheme);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint allocation);
}
