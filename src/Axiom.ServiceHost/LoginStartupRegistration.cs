using System.Runtime.Versioning;
using Axiom.Contracts;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Axiom.ServiceHost;

// A host setting, separate from user-authored automation actions.
[SupportedOSPlatform("windows")]
internal sealed class LoginStartupRegistration(string launcherPath, RegistryKey userRoot, ILoginTaskRegistration? taskRegistration = null)
{
    private readonly ILoginTaskRegistration task = taskRegistration ?? new ElevatedLoginTask();
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Axiom";
    private string Command => $"\"{launcherPath}\" --background";
    private bool Available => Path.IsPathFullyQualified(launcherPath) && AxiomInstallLayout.At(Path.GetDirectoryName(launcherPath)!).Complete;

    public void MigrateLegacy()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        using var key = userRoot.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not null) Set(true); // Preserve the existing enabled setting.
    }

    public LoginStartupStatus Read()
    {
        try
        {
            using var key = userRoot.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var scheduled = task.Read(launcherPath);
            // Legacy Run entries are visible as needing repair, never considered elevated.
            return new(scheduled.Registered || value is not null, scheduled.CurrentLocation, Available);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or COMException)
        { return new(false, false, Available, "无法读取登录启动设置，请检查当前用户权限。"); }
    }

    public LoginStartupStatus Set(bool enabled)
    {
        if (enabled)
        {
            if (!Path.IsPathFullyQualified(launcherPath) || launcherPath.IndexOfAny(['\"', '\r', '\n']) >= 0 || Command.Length > 260)
                throw new InvalidOperationException("程序路径太长或无效，请将完整 Axiom 文件夹移到较短路径后重试。");
            if (!Available) throw new InvalidOperationException("找不到完整的 Axiom 程序，请从交付文件夹打开后重试。");
            task.Set(true, launcherPath);
            using var key = userRoot.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            task.Set(false, launcherPath);
            using var key = userRoot.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        var result = Read();
        if (result.Error is not null || result.Registered != enabled || enabled && !result.CurrentLocation)
            throw new IOException("未能确认登录启动设置，请刷新后核对。");
        return result;
    }
}

internal interface ILoginTaskRegistration
{
    (bool Registered, bool CurrentLocation) Read(string launcherPath);
    void Set(bool enabled, string launcherPath);
}

// Fixed internal launch action only; no task/user/path supplied by UI and no saved credentials.
[SupportedOSPlatform("windows")]
internal sealed class ElevatedLoginTask : ILoginTaskRegistration
{
    private readonly string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException("No user SID.");
    private string Name => "Axiom-" + sid;

    public (bool Registered, bool CurrentLocation) Read(string launcherPath)
    {
        using var objects = new ComObjects();
        dynamic service = objects.Add(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
        service.Connect();
        dynamic folder = objects.Add((object)service.GetFolder("\\"));
        dynamic registered;
        try { registered = objects.Add((object)folder.GetTask(Name)); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { return (false, false); }
        dynamic definition = objects.Add((object)registered.Definition);
        dynamic principal = objects.Add((object)definition.Principal);
        dynamic actions = objects.Add((object)definition.Actions);
        if (actions.Count != 1) return (true, false);
        dynamic action = objects.Add((object)actions.Item(1));
        dynamic triggers = objects.Add((object)definition.Triggers);
        if (action.Type != 0 || triggers.Count != 1) return (true, false);
        dynamic trigger = objects.Add((object)triggers.Item(1));
        return (true, registered.Enabled && principal.RunLevel == 1 && principal.LogonType == 3 &&
            SameUser((string)principal.UserId) &&
            trigger.Type == 9 && SameUser((string)trigger.UserId) &&
            string.Equals((string)action.Path, launcherPath, StringComparison.OrdinalIgnoreCase) &&
            (string)action.Arguments == "--background");
    }

    private bool SameUser(string value)
    {
        if (string.Equals(value, sid, StringComparison.OrdinalIgnoreCase)) return true;
        try { return new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
    }

    public void Set(bool enabled, string launcherPath)
    {
        using var objects = new ComObjects();
        dynamic service = objects.Add(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
        service.Connect();
        dynamic folder = objects.Add((object)service.GetFolder("\\"));
        if (!enabled)
        {
            try { folder.DeleteTask(Name, 0); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { }
            return;
        }
        dynamic definition = objects.Add((object)service.NewTask(0));
        dynamic info = objects.Add((object)definition.RegistrationInfo);
        info.Description = "Axiom：当前用户登录后以管理员权限启动后台自动化。";
        dynamic principal = objects.Add((object)definition.Principal);
        principal.UserId = sid; principal.LogonType = 3; principal.RunLevel = 1;
        dynamic triggers = objects.Add((object)definition.Triggers);
        dynamic trigger = objects.Add((object)triggers.Create(9)); trigger.UserId = sid;
        dynamic settings = objects.Add((object)definition.Settings);
        settings.ExecutionTimeLimit = "PT0S"; settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false; settings.StartWhenAvailable = true; settings.MultipleInstances = 2;
        dynamic actions = objects.Add((object)definition.Actions);
        dynamic action = objects.Add((object)actions.Create(0));
        action.Path = launcherPath; action.Arguments = "--background"; action.WorkingDirectory = Path.GetDirectoryName(launcherPath)!;
        objects.Add((object)folder.RegisterTaskDefinition(Name, definition, 6, sid, null, 3, null));
    }

    private sealed class ComObjects : IDisposable
    {
        private readonly List<object> values = [];
        public object Add(object value) { values.Add(value); return value; }
        public void Dispose()
        {
            for (var index = values.Count - 1; index >= 0; index--)
                if (Marshal.IsComObject(values[index])) Marshal.ReleaseComObject(values[index]);
        }
    }
}
