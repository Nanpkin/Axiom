using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class TaskbarAutoHideRestoreTests
{
    private static TaskInvocation Invocation(string state = "enable") => new(TaskbarAutoHideRestoreCapabilities.Restore, 1,
        ImmutableDictionary<string, string>.Empty.Add("state", state).Add("delaySeconds", "0"));
    [Fact]
    public void WinnerUpdatesRetainFirstBaselineAndReleasePreservesCurrentUnownedFlagsOnce()
    {
        var api = new Api { Flags = 2 }; var setting = new TaskbarAutoHideRestoration(api); Assert.Equal(0, api.Writes);
        Assert.True(setting.Apply(true, default).Owned); Assert.Equal(3u, api.Flags);
        Assert.True(setting.Apply(false, default).Owned); Assert.Equal(2u, api.Flags);
        Assert.True(setting.Apply(true, default).Owned); Assert.Equal(3u, api.Flags);
        api.Flags = 1; // An unrelated flag changed; restore only our autohide bit.
        var receipt = setting.Release(default); Assert.True(receipt.Success); Assert.False(receipt.Owned); Assert.Equal(0u, api.Flags); Assert.Equal(4, api.Writes);
        Assert.True(setting.Release(default).Success); Assert.Equal(4, api.Writes);
        Assert.False(setting.Apply(true, default).Owned); Assert.Equal(4, api.Writes);
    }
    [Fact]
    public void UnknownBaselineNeverWritesAndExternalAutohideOrShellReplacementRelinquishesWithoutRestoration()
    {
        var unknown = new Api { Flags = null }; Assert.Throws<IOException>(() => new TaskbarAutoHideRestoration(unknown)); Assert.Equal(0, unknown.Writes);
        var external = new Api(); var transaction = new TaskbarAutoHideRestoration(external); Assert.True(transaction.Apply(true, default).Owned);
        external.Flags = 0; Assert.False(transaction.Apply(false, default).Owned); Assert.True(transaction.Release(default).Success); Assert.Equal(1, external.Writes); Assert.Equal(0u, external.Flags);
        var replaced = new Api(); transaction = new(replaced); Assert.True(transaction.Apply(true, default).Owned); replaced.Current = false;
        var release = transaction.Release(default); Assert.False(release.Success); Assert.False(release.Owned); Assert.Equal(1, replaced.Writes); Assert.Equal(1u, replaced.Flags);
        var lost = new Api(); transaction = new(lost); lost.AfterWrite = () => lost.Flags = null;
        Assert.False(transaction.Apply(true, default).Owned); Assert.True(transaction.Release(default).Success); Assert.Equal(1, lost.Writes);
    }
    [Fact(Timeout = 10000)]
    public async Task CanceledUnconfirmedWriteRelinquishesAndBlockedNativeWorkRetainsCapturedHandleUntilTrueCleanup()
    {
        using var stop = new CancellationTokenSource(); var api = new Api { AfterWrite = stop.Cancel }; var setting = new TaskbarAutoHideRestoration(api);
        Assert.False(setting.Apply(true, stop.Token).Owned); Assert.True(setting.Release(default).Success); Assert.Equal(1u, api.Flags); Assert.Equal(1, api.Writes);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api = new Api { AfterWrite = () => { entered.SetResult(); release.Wait(); } };
        using var lifetime = new CancellationTokenSource(); var session = new Session(api, new("任务栏恢复", TimeSpan.FromSeconds(5)));
        var applying = session.ApplyAsync(Invocation().Parameters, lifetime.Token); Task? cleanup = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); lifetime.Cancel(); var result = await applying;
            Assert.False(result.Success); Assert.False(result.Owned); Assert.Contains("取消", result.Reason);
            cleanup = session.DisposeAsync().AsTask(); Assert.False(cleanup.IsCompleted); Assert.False(api.Disposed); Assert.Equal(1, api.Writes);
        }
        finally { release.Set(); await (cleanup ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.True(api.Disposed); Assert.Equal(1u, api.Flags); Assert.Equal(1, api.Writes);
    }
    [Fact]
    public async Task RealRestoreRegistrationUsesDesiredOnlyStateConditionsAndStrictImportSaveKeepsOneShotSchema()
    {
        var plugins = BuiltInAutomationPlugins.Create(); var compiler = new AutomationTaskCompiler();
        foreach (var plugin in plugins) { foreach (var d in plugin.Detections) compiler.Register(d); foreach (var a in plugin.Actions) compiler.Register(a); }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. plugins.SelectMany(p => p.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var pluginRestore = Assert.Single(plugins.OfType<TaskbarAutoHideRestorePlugin>());
        Assert.DoesNotContain(pluginRestore.Combinations, c => c.DetectionId is HotkeyCapabilities.Pressed or SystemEventCapabilities.Event);
        Assert.True(Assert.Single(pluginRestore.Bindings).Descriptor.Observe);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "maximized-taskbar-auto-hide-restore"); Assert.True(template.Draft.Paused);
        var path = Path.Combine(Path.GetTempPath(), "SyntheticEditor.exe");
        var detection = new TaskInvocation(WindowStateDetectionCapabilities.StateIs, 1, ImmutableDictionary<string, string>.Empty.Add("executable", path).Add("state", "maximized"));
        var draft = template.Draft with { Detection = detection }; Assert.Equal(RuleActionKind.Desired, compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
        var action = Assert.Single(pluginRestore.Actions); Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation() with { Parameters = Invocation().Parameters.SetItem("delaySeconds", "12") }).MinimumDuration);
        Assert.Equal(RuleActionKind.Command, new TaskbarAutoHideTaskAction().Compile(Invocation() with { CapabilityId = TaskbarAutoHideCapabilities.Set }, ResourceScope.Session("owner", 1)).Kind);
        var now = DateTimeOffset.UtcNow; var recorder = new UsageRecorder(new(5, false, true, []), now); recorder.Add(new(now, "process-start", path));
        var proposed = new UsageImport(1, recorder.Session.Id, "existing", "合成条件恢复", "合成导入", detection with { Parameters = detection.Parameters.SetItem("executable", recorder.Session.Objects[0].Id) }, Invocation());
        string Proposal(TaskInvocation settings) => JsonSerializer.Serialize(proposed with { Action = settings });
        var imported = UsageAnalysis.Import(Proposal(Invocation()), recorder.Session, catalog).Draft!; Assert.True(imported.Paused);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation("toggle")), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation() with { Parameters = Invocation().Parameters.Add("restoreAnyShell", "true") }), recorder.Session, catalog));
        var db = Path.Combine(Path.GetTempPath(), "axiom-taskbar-restore-" + Guid.NewGuid().ToString("N") + ".db");
        try { using var store = new SqliteAutomationTaskStore(db); await store.SaveAsync(imported, default); Assert.Equal(TaskbarAutoHideRestoreCapabilities.Restore, Assert.Single(await store.ListAsync()).Action.CapabilityId); }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(db + suffix); }
    }
    private sealed class Api : ITaskbarAutoHideApi
    {
        internal bool Current = true, Disposed; internal uint? Flags = 0; internal int Writes; internal Action? AfterWrite;
        public bool IsCurrent => Current;
        public uint? ReadState() => Flags;
        public bool SetState(uint state) { Writes++; Flags = state; AfterWrite?.Invoke(); return true; }
    }
    private sealed class Session(Api api, BoundedNativeCommand commands) : SerializedRestorableSettingSession(commands)
    {
        private readonly TaskbarAutoHideRestoration setting = new(api);
        protected override RestorableSettingResult ApplyCore(IReadOnlyDictionary<string, string> parameters, CancellationToken token) => setting.Apply(parameters["state"] == "enable", token);
        protected override RestorableSettingResult ReleaseCore(CancellationToken token) => setting.Release(token);
        protected override void RelinquishCore() => setting.Relinquish();
        protected override void DisposeCore() => api.Disposed = true;
    }
}
