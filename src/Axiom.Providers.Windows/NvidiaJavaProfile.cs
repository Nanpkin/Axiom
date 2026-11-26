using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

// NVAPI DRS v1 structures (nvapi.h, pack 4); load only the installed x64 driver.
public static unsafe class NvidiaJavaProfile
{
    private const uint PresentMethod = 0x20D690F8;
    private const uint PreferNative = 0; // OGL_CPL_PREFER_DXPRESENT_PREFER_DISABLED
    private const string Application = "javaw.exe";
    private static readonly object Gate = new();

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint Query(uint id);

    private static nint Function(uint id)
    {
        var address = Query(id);
        return address != 0 ? address : throw new InvalidOperationException("当前 NVIDIA 驱动缺少所需的配置接口。");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ApplicationV1
    {
        public uint Version, Predefined;
        public fixed char Name[2048], FriendlyName[2048], Launcher[2048];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SettingV1
    {
        public uint Version;
        public fixed char Name[2048];
        public uint Id, Type, Location, IsPredefined, PredefinedValid;
        public fixed byte PredefinedValue[4100];
        public uint CurrentValue;
        public fixed byte CurrentPadding[4096];
    }

    private static void Check(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"{operation}失败（NVAPI {status}）。");
    }

    private sealed class Session : IDisposable
    {
        public nint Handle { get; }
        public Session()
        {
            nint session = 0;
            Check(((delegate* unmanaged[Cdecl]<nint*, int>)Function(0x0694d52e))(&session), "创建 NVIDIA 会话");
            Handle = session;
        }
        public void Dispose() => ((delegate* unmanaged[Cdecl]<nint, int>)Function(0xdad9cff8))(Handle);

        public nint LoadJavaProfile(string? requestedPath, CancellationToken token)
        {
            Check(((delegate* unmanaged[Cdecl]<nint, int>)Function(0x375dbd6b))(Handle), "读取驱动配置");
            var profile = FindPathProfile(requestedPath, token);
            if (profile == 0 && !string.IsNullOrEmpty(requestedPath))
                throw new InvalidOperationException("驱动中没有登记该 Java 路径，请核对 NVIDIA App 的程序配置。");
            if (profile == 0) profile = FindByName();
            nint baseProfile = 0, globalProfile = 0;
            Check(((delegate* unmanaged[Cdecl]<nint,nint*,int>)Function(0xda8466a0))(Handle, &baseProfile), "识别基础配置");
            Check(((delegate* unmanaged[Cdecl]<nint,nint*,int>)Function(0x617bff9f))(Handle, &globalProfile), "识别全局配置");
            if (profile == 0 || profile == globalProfile || profile == baseProfile)
                throw new InvalidOperationException("未确认 javaw.exe 的独立程序配置。");
            return profile;
        }

        // NVIDIA App can register full paths which FindApplicationByName("javaw.exe") misses.
        private nint FindPathProfile(string? requestedPath, CancellationToken token)
        {
            var enumProfile = (delegate* unmanaged[Cdecl]<nint,uint,nint*,int>)Function(0xbc371ee0);
            var enumApp = (delegate* unmanaged[Cdecl]<nint,nint,uint,uint*,ApplicationV1*,int>)Function(0x7fa2173a);
            nint match = 0;
            ApplicationV1 entry = default;
            for (uint i = 0; ; i++)
            {
                token.ThrowIfCancellationRequested();
                nint current = 0;
                var status = enumProfile(Handle, i, &current);
                if (status == -7) return match;
                Check(status, "枚举驱动配置");
                for (uint j = 0; ; j++)
                {
                    uint count = 1;
                    entry.Version = (uint)sizeof(ApplicationV1) | 1u << 16;
                    status = enumApp(Handle, current, j, &count, &entry);
                    if (status == -7) break;
                    Check(status, "枚举程序配置");
                    if (count == 0) break;
                    var path = new string(entry.Name);
                    if (!Path.IsPathFullyQualified(path) || !Application.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(requestedPath) && !Path.GetFullPath(path).Equals(requestedPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (match != 0 && match != current) throw new InvalidOperationException("检测到多个 Java 程序配置，请在工具箱选择 Minecraft 使用的 javaw.exe。");
                    match = current;
                }
            }
        }

        private nint FindByName()
        {
            ApplicationV1 app = default;
            app.Version = (uint)sizeof(ApplicationV1) | 1u << 16;
            nint profile = 0;
            char* name = stackalloc char[2048];
            Application.AsSpan().CopyTo(new Span<char>(name, Application.Length));
            name[Application.Length] = '\0';
            int result = ((delegate* unmanaged[Cdecl]<nint, char*, nint*, ApplicationV1*, int>)Function(0xeee566b2))(Handle, name, &profile, &app);
            if (result == -166) throw new InvalidOperationException("未找到 javaw.exe 的 NVIDIA 程序配置，请先在 NVIDIA App 中添加此程序。");
            Check(result, "查找 javaw.exe 程序配置");
            if (!Application.Equals(new string(app.Name), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("未确认 javaw.exe 的独立程序配置。");
            return profile;
        }

        public bool IsNative(nint profile)
        {
            SettingV1 setting = default;
            setting.Version = (uint)sizeof(SettingV1) | 1u << 16;
            var result = ((delegate* unmanaged[Cdecl]<nint,nint,uint,SettingV1*,int>)Function(0x73bf8338))(Handle, profile, PresentMethod, &setting);
            if (result == -160) return false;
            Check(result, "读取呈现方法");
            if (setting.Id != PresentMethod || setting.Type != 0) throw new InvalidOperationException("驱动返回了未知的呈现方法格式。");
            return setting.Location == 0 && setting.CurrentValue == PreferNative;
        }

        public void SaveNative(nint profile, CancellationToken token)
        {
            SettingV1 setting = default;
            setting.Version = (uint)sizeof(SettingV1) | 1u << 16;
            setting.Id = PresentMethod;
            setting.CurrentValue = PreferNative;
            Check(((delegate* unmanaged[Cdecl]<nint,nint,SettingV1*,int>)Function(0x577dd202))(Handle, profile, &setting), "设置优先本机");
            token.ThrowIfCancellationRequested();
            Check(((delegate* unmanaged[Cdecl]<nint,int>)Function(0xfcbc7e14))(Handle), "保存程序配置");
        }
    }

    public static string EnsureNative(string? requestedPath, CancellationToken token)
    {
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            Check(((delegate* unmanaged[Cdecl]<int>)Function(0x0150E828))(), "初始化 NVIDIA 驱动");
            try
            {
                using (var session = new Session())
                {
                    var profile = session.LoadJavaProfile(requestedPath, token);
                    if (session.IsNative(profile)) return "javaw.exe 的程序配置已是优先本机。";
                    session.SaveNative(profile, token);
                }
                using var verify = new Session();
                if (!verify.IsNative(verify.LoadJavaProfile(requestedPath, token))) throw new InvalidOperationException("保存后未读回优先本机。");
                return "已将 javaw.exe 的 Vulkan/OpenGL 呈现方法设为优先本机，并重新读取确认。";
            }
            finally { ((delegate* unmanaged[Cdecl]<int>)Function(0xd22bdd7e))(); }
        }
    }
}
