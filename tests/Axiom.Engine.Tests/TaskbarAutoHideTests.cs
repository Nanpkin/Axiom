using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class TaskbarAutoHideTests
{
    private static TaskInvocation Invocation(string state = "enable", string delay = "0") => new(TaskbarAutoHideCapabilities.Set, 1,
        ImmutableDictionary<string, string>.Empty.Add("state", state).Add("delaySeconds", delay));
    [Fact]
    public async Task SdkAbiRealRegistrationStrictImportAndPausedShortcutSaveUseOneArbitratedResource()
    {
        Assert.Equal(IntPtr.Size == 8 ? 48 : 36, Marshal.SizeOf<TaskbarAppBarData>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<TaskbarAppBarData>(nameof(TaskbarAppBarData.Window)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 32, Marshal.OffsetOf<TaskbarAppBarData>(nameof(TaskbarAppBarData.Parameter)).ToInt32());
        var action = new TaskbarAutoHideTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation(delay: "12")).MinimumDuration);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("disable"), scope).Key);
        Assert.Equal("system-taskbar", action.Compile(Invocation(), scope).Key.ResourceId);
        foreach (var invalid in new[] { "toggle", "ENABLE", "hidden", "" }) Assert.Throws<ArgumentException>(() => TaskbarAutoHideCapabilities.Validate(Invocation(invalid)));
        foreach (var invalid in new[] { "-1", "1.5", "86401" }) Assert.Throws<ArgumentException>(() => TaskbarAutoHideCapabilities.Validate(Invocation(delay: invalid)));
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<TaskbarAutoHidePlugin>());
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        Assert.Contains(new TaskCombination(HotkeyCapabilities.Pressed, TaskbarAutoHideCapabilities.Set), plugin.Combinations);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "taskbar-auto-hide-shortcut"); Assert.True(template.Draft.Paused);
        var detection = new TaskInvocation(HotkeyCapabilities.Pressed, 1, ImmutableDictionary<string, string>.Empty.Add("chords", "Ctrl+Alt+F9"));
        Assert.Equal(RuleActionKind.Command, compiler.Compile(template.Draft with { Detection = detection }, "owner", scope).Action.Kind);
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        string Proposal(TaskInvocation command) => JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id, "existing", "合成任务栏设置", "合成导入", detection, command));
        var draft = UsageAnalysis.Import(Proposal(Invocation("disable")), recorder.Session, catalog).Draft!; Assert.True(draft.Paused);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation("hidden")), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation() with { Parameters = Invocation().Parameters.Add("window", "arbitrary") }), recorder.Session, catalog));
        var db = Path.Combine(Path.GetTempPath(), "axiom-taskbar-" + Guid.NewGuid().ToString("N") + ".db");
        try { using var store = new SqliteAutomationTaskStore(db); await store.SaveAsync(draft, default); Assert.Equal("disable", Assert.Single(await store.ListAsync()).Action.Parameters["state"]); }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(db + suffix); }
    }
    [Fact]
    public void ZeroIsKnownOffAndBothDirectionsChangeOnlyAutohideOnceWhileConsistentStateIsNoOp()
    {
        foreach (var flags in new uint[] { 0, 1, 2, 3 })
        {
            var enabled = (flags & 1) == 0; var api = new Api { Flags = flags };
            var result = TaskbarAutoHideOperation.Apply(api, enabled, default);
            Assert.True(result.Success); Assert.Equal(flags ^ 1, api.Flags); Assert.Equal(1, api.Writes); Assert.Equal(2, api.Reads);
            Assert.Equal(flags & 2, api.Flags!.Value & 2); if (enabled) Assert.Contains("边缘", result.Reason);
            var noop = new Api { Flags = flags }; Assert.True(TaskbarAutoHideOperation.Apply(noop, !enabled, default).Success);
            Assert.Equal(0, noop.Writes); Assert.Equal(1, noop.Reads); Assert.Equal(flags, noop.Flags);
        }
    }
    [Fact]
    public void UnknownOrReplacedShellNeverWritesAndDeniedOrUnconfirmedRequestNeverRetriesOrRestores()
    {
        var absent = new Api { Current = false }; Assert.False(TaskbarAutoHideOperation.Apply(absent, true, default).Success); Assert.Equal(0, absent.Reads); Assert.Equal(0, absent.Writes);
        var unknown = new Api { Flags = null }; Assert.False(TaskbarAutoHideOperation.Apply(unknown, true, default).Success); Assert.Equal(0, unknown.Writes);
        var replaced = new Api(); replaced.AfterRead = () => replaced.Current = false;
        Assert.False(TaskbarAutoHideOperation.Apply(replaced, true, default).Success); Assert.Equal(0, replaced.Writes);
        var denied = new Api { RefuseWrite = true }; Assert.False(TaskbarAutoHideOperation.Apply(denied, true, default).Success); Assert.Equal(1, denied.Writes); Assert.Equal(1, denied.Reads);
        var restarted = new Api(); restarted.AfterWrite = () => restarted.Current = false;
        var result = TaskbarAutoHideOperation.Apply(restarted, true, default); Assert.False(result.Success); Assert.Contains("新任务栏", result.Reason); Assert.Equal(1u, restarted.Flags); Assert.Equal(1, restarted.Reads);
        var unreadable = new Api(); unreadable.AfterWrite = () => unreadable.Flags = null;
        Assert.False(TaskbarAutoHideOperation.Apply(unreadable, true, default).Success); Assert.Equal(1, unreadable.Writes); Assert.Equal(2, unreadable.Reads);
        var manual = new Api(); manual.AfterWrite = () => manual.Flags = 0;
        result = TaskbarAutoHideOperation.Apply(manual, true, default); Assert.False(result.Success); Assert.Contains("读回不一致", result.Reason); Assert.Equal(0u, manual.Flags); Assert.Equal(1, manual.Writes);
    }
    [Fact(Timeout = 10000)]
    public async Task CancelAndDeadlineKeepSingleNativeAdmissionUntilAnAlreadySentRequestReallyEnds()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Api();
        Assert.ThrowsAny<OperationCanceledException>(() => TaskbarAutoHideOperation.Apply(untouched, true, before.Token)); Assert.Equal(0, untouched.Reads); Assert.Equal(0, untouched.Writes);
        using var afterRead = new CancellationTokenSource(); var noWrite = new Api { AfterRead = afterRead.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => TaskbarAutoHideOperation.Apply(noWrite, true, afterRead.Token)); Assert.Equal(0, noWrite.Writes);
        using var afterWrite = new CancellationTokenSource(); var written = new Api { AfterWrite = afterWrite.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => TaskbarAutoHideOperation.Apply(written, true, afterWrite.Token)); Assert.Equal(1u, written.Flags); Assert.Equal(1, written.Writes); Assert.Equal(1, written.Reads);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { AfterWrite = () => { entered.SetResult(); release.Wait(); } };
        var time = new ManualTimeProvider(); var commands = new BoundedNativeCommand("任务栏", TimeSpan.FromSeconds(5));
        var request = commands.RunAsync(token => TaskbarAutoHideOperation.Apply(api, true, token), default, time);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); time.Advance(TimeSpan.FromSeconds(5)); var timedOut = await request;
            Assert.False(timedOut.Success); Assert.Contains("超时", timedOut.Reason); Assert.False(commands.DrainAsync().IsCompleted);
            var overlap = await commands.RunAsync(token => TaskbarAutoHideOperation.Apply(api, false, token), default);
            Assert.False(overlap.Success); Assert.Contains("不排队", overlap.Reason); Assert.Equal(1u, api.Flags); Assert.Equal(1, api.Writes);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.Equal(1u, api.Flags); Assert.Equal(1, api.Writes); Assert.Equal(1, api.Reads);
    }
    private sealed class Api : ITaskbarAutoHideApi
    {
        internal bool Current = true, RefuseWrite; internal uint? Flags = 0; internal int Reads, Writes; internal Action? AfterRead, AfterWrite;
        public bool IsCurrent => Current;
        public uint? ReadState() { Reads++; var value = Flags; AfterRead?.Invoke(); return value; }
        public bool SetState(uint flags) { Writes++; if (RefuseWrite) return false; Flags = flags; AfterWrite?.Invoke(); return true; }
    }
}
