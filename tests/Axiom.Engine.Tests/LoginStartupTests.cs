using Axiom.Contracts;
using Axiom.ServiceHost;
using Microsoft.Win32;

namespace Axiom.Engine.Tests;

public sealed class LoginStartupTests
{
    [Fact]
    public void StartupProtocolOnlyAcceptsBooleanSettingAndRejectsFieldsBeforeSideEffects()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new AutomationMessage(1, "set-startup", "test", now.AddSeconds(10), StartupEnabled: true);
        AutomationProtocol.ValidateRequest(request, now);
        AutomationProtocol.ValidateRequest(request with { StartupEnabled = false }, now);
        AutomationProtocol.ValidateRequest(request with { Type = "read-startup", StartupEnabled = null }, now);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { StartupEnabled = null }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Id = "C:\\other.exe" }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Type = "read" }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Startup = new(false, false, false) }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { DeadlineUtc = now }, now));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RegistrationQuotesPathRepairsMovedLocationAndDisablesOnlyAxiom()
    {
        if (!OperatingSystem.IsWindows()) return;
        var registryPath = @"Software\Axiom.Tests\" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "Axiom startup " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var launcher = Path.Combine(directory, "Axiom.exe");
            foreach (var relative in new[] { "Axiom.exe", "backend/host/Axiom.ServiceHost.exe", "backend/agent/Axiom.SessionAgent.exe", "ui/Axiom.WinUI.exe" })
            {
                var path = Path.Combine(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
            }
            using var root = Registry.CurrentUser.CreateSubKey(registryPath);
            using var run = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            run.SetValue("OtherApp", "preserve");
            var tasks = new MemoryLoginTask();
            var setting = new LoginStartupRegistration(launcher, root, tasks);
            Assert.False(setting.Read().Registered);
            Assert.True(setting.Set(true).CurrentLocation);
            Assert.Equal(launcher, tasks.Path); Assert.Null(run.GetValue("Axiom"));
            setting.Set(true); // Repeated enable is idempotent.
            run.SetValue("Axiom", "\"C:\\old folder\\Axiom.exe\" --background");
            tasks.Path = "C:\\old folder\\Axiom.exe";
            Assert.True(setting.Read().Registered); Assert.False(setting.Read().CurrentLocation);
            Assert.True(setting.Set(true).CurrentLocation);
            File.Delete(launcher);
            Assert.Throws<InvalidOperationException>(() => setting.Set(true));
            Assert.True(setting.Read().Registered); // Missing files do not silently erase the previous setting.
            Assert.False(setting.Set(false).Registered);
            Assert.False(setting.Set(false).Registered);
            Assert.Equal("preserve", run.GetValue("OtherApp"));
            Assert.Throws<InvalidOperationException>(() => new LoginStartupRegistration("C:\\" + new string('a', 260) + "\\Axiom.exe", root, tasks).Set(true));
            Assert.Null(run.GetValue("Axiom"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MemoryLoginTask : ILoginTaskRegistration
    {
        public string? Path { get; set; }
        public (bool Registered, bool CurrentLocation) Read(string launcherPath) => (Path is not null, Path == launcherPath);
        public void Set(bool enabled, string launcherPath) => Path = enabled ? launcherPath : null;
    }
}
