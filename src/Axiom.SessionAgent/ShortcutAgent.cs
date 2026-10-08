using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Axiom.Contracts;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class ShortcutAgent : IDisposable
{
    private readonly string sid;
    private readonly WindowsGlobalHotkeys listener = new();
    private readonly ShortcutRegistrationSet registrations;
    internal HotkeyRegistrationHub Hotkeys { get; }
    internal Task<T> OnMessageThreadAsync<T>(Func<T> action) => listener.ExecuteAsync(action);
    private readonly HashSet<Task> clients = [];
    public ShortcutAgent(string sid)
    {
        this.sid = sid;
        var actions = new ShortcutActionRegistry();
        actions.Register(new(KeyboardShortcuts.ShowWindow, "显示 / 关闭 Axiom 窗口", "按一次打开 Axiom，再按一次关闭窗口；后台继续运行。"), ToggleWindow);
        Hotkeys = new(new Native(listener));
        registrations = new(actions, new SharedNative(this));
        listener.Pressed += Hotkeys.Press;
    }
    private sealed class SharedNative(ShortcutAgent owner) : IShortcutNative
    {
        public bool Register(int id, ShortcutChord chord) => owner.Hotkeys.Register("app:" + id, chord, () => owner.registrations.Press(id));
        public void Unregister(int id) => owner.Hotkeys.Release("app:" + id);
    }
    private sealed class Native(WindowsGlobalHotkeys listener) : IShortcutNative
    {
        public bool Register(int id, ShortcutChord chord)
        {
            return listener.Register(id, HotkeyCapabilities.Modifiers(chord), HotkeyCapabilities.VirtualKey(chord));
        }
        public void Unregister(int id) => listener.Unregister(id);
    }
    private static void ToggleWindow()
    {
        var layout = AxiomInstallLayout.ForComponent(AppContext.BaseDirectory);
        var root = layout.Root;
        var uiPath = layout.Ui;
        foreach (var ui in Process.GetProcessesByName("Axiom.WinUI"))
        {
            using (ui)
            {
                if (ui.SessionId != Process.GetCurrentProcess().SessionId) continue;
                if (!string.Equals(ui.MainModule?.FileName, uiPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("另一份 Axiom 窗口正在运行，请先关闭旧版本。");
                var handle = ui.MainWindowHandle;
                if (handle != 0 && !ui.CloseMainWindow())
                    throw new InvalidOperationException("无法关闭 Axiom 窗口，请稍后重试。");
                return;
            }
        }
        var launcher = Path.Combine(root, "Axiom.exe");
        if (!File.Exists(launcher)) throw new FileNotFoundException("找不到完整的 Axiom 程序，请保留交付文件夹。");
        using var launched = Process.Start(new ProcessStartInfo(launcher) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("无法打开 Axiom。");
        AllowSetForegroundWindow((uint)launched.Id);
    }
    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = GameHdrLocalPipeFactory.Create(KeyboardShortcuts.AgentPipe(sid), sid);
            try { await pipe.WaitForConnectionAsync(token); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            clients.RemoveWhere(x => x.IsCompleted);
            if (clients.Count >= 8) { pipe.Dispose(); continue; }
            clients.Add(HandleAsync(pipe, token));
        }
        await Task.WhenAll(clients);
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(12)); AutomationMessage? request = null;
            try
            {
                request = await AutomationProtocol.ReadAsync(pipe, timeout.Token);
                string? clientSid = null; pipe.RunAsClient(() => clientSid = WindowsIdentity.GetCurrent().User?.Value);
                if (clientSid != sid) throw new UnauthorizedAccessException();
                GameHdrPipePeer.RequireHost(pipe);
                AutomationProtocol.ValidateRequest(request, DateTimeOffset.UtcNow, shortcutAgent: true);
                var snapshot = await listener.ExecuteAsync(() => request.Type == "read-shortcuts" ? registrations.Read() :
                    registrations.Replace(request.Bindings!, request.Type == "restore-shortcuts"));
                await AutomationProtocol.WriteAsync(pipe, new(1, "shortcuts", request.RequestId, Shortcuts: snapshot), timeout.Token);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                try { await AutomationProtocol.WriteAsync(pipe, new(1, "error", request?.RequestId ?? "rejected", Error: error.Message), timeout.Token); }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException) { }
            }
        }
    }
    public async Task RestoreFromHostAsync(CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", AutomationProtocol.PipeName(sid), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await pipe.ConnectForUserAsync(3000, timeout.Token);
        var id = Guid.NewGuid().ToString("N");
        await AutomationProtocol.WriteAsync(pipe, new(1, "restore-shortcuts", id, DateTimeOffset.UtcNow.AddSeconds(12)), timeout.Token);
        var response = await AutomationProtocol.ReadAsync(pipe, timeout.Token);
        if (response.Type != "shortcuts" || response.RequestId != id) throw new IOException("快捷键恢复未确认。");
    }
    public void Dispose() => listener.Dispose();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
