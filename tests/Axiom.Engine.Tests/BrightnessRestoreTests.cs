using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[Collection("Brightness native ownership")]
public sealed class BrightnessRestoreTests
{
    private static Dictionary<string, string> Parameters(int percent) => new() { ["percent"] = percent.ToString(System.Globalization.CultureInfo.InvariantCulture), ["delaySeconds"] = "0", ["target"] = "primary" };
    [Fact(Timeout = 10000)]
    public async Task RegisteredDesiredTemplatesSaveAndImportUseOneCapturedBaselineAcrossUpdatesAndNativeOwnershipUntilRelease()
    {
        var modules = BuiltInAutomationPlugins.Create(); var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var compiler = new AutomationTaskCompiler(); foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        foreach (var id in new[] { "battery-low-brightness-restore", "standard-saver-hardware-brightness-restore" })
        {
            var draft = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == id).Draft; Assert.True(draft.Paused);
            var rule = compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)); Assert.Equal(RuleActionKind.Desired, rule.Action.Kind); Assert.Equal("brightness", rule.Action.Key.ResourceId);
            var proposal = new UsageImport(1, recorder.Session.Id, "existing", "brightness restoration", "synthetic", draft.Detection, draft.Action);
            Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = draft.Action with { Parameters = draft.Action.Parameters.SetItem("percent", "101") } }), recorder.Session, catalog));
            var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-restore-brightness-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(draft, default);
                using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync()); Assert.Equal(draft.Action.Parameters, loaded.Action.Parameters); Assert.NotNull(compiler.Compile(loaded, "owner", rule.Scope));
            }
            finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f); }
        }
        await Assert.ThrowsAsync<IOException>(() => BrightnessRestoreSession.CreateAsync(_ => Capture([], () => { }), "primary", TimeSpan.FromSeconds(10), default));
        await BrightnessNativeOwnership.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); // unknown/empty baseline never owns a write target
        var target = new Target(80); var captures = 0; var closed = 0; var captureThread = 0;
        var session = await BrightnessRestoreSession.CreateAsync(_ => { captures++; captureThread = Environment.CurrentManagedThreadId; return Capture([target], () => { closed++; Assert.Equal(captureThread, Environment.CurrentManagedThreadId); }); }, "primary", TimeSpan.FromSeconds(10), default);
        try
        {
            Assert.True((await session.ApplyAsync(Parameters(30), default)).Owned); Assert.Null(BrightnessNativeOwnership.TryAcquire());
            Assert.True((await session.ApplyAsync(Parameters(50), default)).Owned); Assert.Equal(1, captures); Assert.Equal(50u, target.Current);
            Assert.True((await session.ReleaseAsync(default)).Success); Assert.Equal(80u, target.Current); Assert.Equal(new uint[] { 30, 50, 80 }, target.Writes); Assert.Equal(1, closed);
        }
        finally { await session.DisposeAsync(); }
        using var next = BrightnessNativeOwnership.TryAcquire(); Assert.NotNull(next);
    }
    [Fact(Timeout = 10000)]
    public async Task ExternalChangeUnknownTopologyPartialReadbackAndSelectionChangeNeverRecaptureOrRestoreGuessedTargets()
    {
        var first = new Target(80); var second = new Target(70); var closed = 0;
        var session = await BrightnessRestoreSession.CreateAsync(_ => Capture([first, second], () => closed++), "primary", TimeSpan.FromSeconds(10), default);
        try
        {
            Assert.True((await session.ApplyAsync(Parameters(30), default)).Success); first.Current = 45;
            var released = await session.ReleaseAsync(default); Assert.True(released.Success); Assert.Contains("1块其他来源", released.Reason);
            Assert.Equal(45u, first.Current); Assert.Equal(70u, second.Current); Assert.Equal(new uint[] { 30 }, first.Writes);
        }
        finally { await session.DisposeAsync(); }
        Assert.Equal(1, closed);
        foreach (var unknown in new[] { false, true })
        {
            var target = new Target(80); var lease = await BrightnessRestoreSession.CreateAsync(_ => Capture([target], () => { }), "primary", TimeSpan.FromSeconds(10), default);
            try
            {
                Assert.True((await lease.ApplyAsync(Parameters(30), default)).Owned);
                if (unknown) target.Unknown = true; else target.RefuseReadback = true;
                var result = await lease.ApplyAsync(Parameters(50), default); Assert.False(result.Owned);
                Assert.False((await lease.ApplyAsync(Parameters(60), default)).Success);
                await lease.ReleaseAsync(default);
                Assert.DoesNotContain(80u, target.Writes); Assert.DoesNotContain(60u, target.Writes);
            }
            finally { await lease.DisposeAsync(); }
        }
        var partialA = new Target(80); var partialB = new Target(70) { RefuseReadback = true };
        var partial = await BrightnessRestoreSession.CreateAsync(_ => Capture([partialA, partialB], () => { }), "primary", TimeSpan.FromSeconds(10), default);
        try { var result = await partial.ApplyAsync(Parameters(30), default); Assert.False(result.Owned); Assert.Contains("部分完成", result.Reason); await partial.ReleaseAsync(default); Assert.Equal(80u, partialA.Current); Assert.DoesNotContain(70u, partialB.Writes); }
        finally { await partial.DisposeAsync(); }
        var selected = new Target(80); var fixedLease = await BrightnessRestoreSession.CreateAsync(_ => Capture([selected], () => { }), "primary", TimeSpan.FromSeconds(10), default);
        try { Assert.False((await fixedLease.ApplyAsync(new Dictionary<string, string>(Parameters(30)) { ["target"] = "all" }, default)).Owned); Assert.Empty(selected.Writes); }
        finally { await fixedLease.DisposeAsync(); }
    }
    [Fact(Timeout = 15000)]
    public async Task CanceledOrTimedOutNativeQueryAndWriteRetainCapturedResourcesAndAdmissionUntilActualReturn()
    {
        foreach (var timeout in new[] { false, true }) foreach (var write in new[] { false, true })
        {
            var time = new ManualTimeProvider(); var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); time.TimerCreated = _ => timer.TrySetResult();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var unblock = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
            var target = new Target(80); var later = new Target(70); var closed = 0;
            Action block = () => { entered.TrySetResult(); unblock.Wait(); };
            if (write) target.AfterWrite = block; else target.AfterRead = block;
            var session = await BrightnessRestoreSession.CreateAsync(_ => Capture([target, later], () => closed++), "primary", TimeSpan.FromSeconds(10), default, time);
            timer = new(TaskCreationOptions.RunContinuationsAsynchronously); // ignore capture's admission timer; observe the apply deadline
            var applying = session.ApplyAsync(Parameters(30), stop.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await timer.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (timeout) time.Advance(TimeSpan.FromSeconds(10)); else stop.Cancel();
                var failed = await applying; Assert.False(failed.Owned); Assert.False(failed.Success); Assert.Equal(0, closed); Assert.Null(BrightnessNativeOwnership.TryAcquire());
                var closing = session.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); unblock.Set(); await closing.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(1, closed); Assert.Empty(later.Writes); Assert.Equal(write ? 1 : 0, target.Writes.Count); Assert.DoesNotContain(80u, target.Writes);
            }
            finally { unblock.Set(); await session.DisposeAsync(); }
        }
    }
    [Fact]
    public async Task ApplyReceiptIsVisibleOnlyAfterAdmissionIsReleasedForSuccessfulAndFailedOutcomes()
    {
        foreach (var success in new[] { true, false })
        {
            using var admission = new SemaphoreSlim(0, 1);
            // Inline the consumer to force it to inspect admission before the publishing call returns.
            var completion = new TaskCompletionSource<RestorableSettingResult>();
            var observer = completion.Task.ContinueWith(received =>
            {
                Assert.Equal(success, received.Result.Success);
                if (!admission.Wait(0)) return false;
                admission.Release(); return true;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            BrightnessRestoreSession.CompleteApply(admission, completion, () =>
            {
                Assert.False(admission.Wait(0)); // the native operation still owns admission while it runs
                return new(success, "synthetic result", success);
            });
            Assert.True(await observer);
        }
    }
    private static BrightnessRestoreCapture Capture(Target[] targets, Action close) => new(targets.Select(t => new BrightnessRestoreTarget(t.Current!.Value, p => (uint)p, t.Read, t.Write)).ToArray(), 0, 0, _ => { }, close);
    private sealed class Target(uint initial)
    {
        internal uint? Current = initial; internal bool Unknown, RefuseReadback; internal Action? AfterWrite, AfterRead;
        internal readonly List<uint> Writes = [];
        internal uint? Read() { AfterRead?.Invoke(); return Unknown ? null : Current; }
        internal bool Write(uint value) { Writes.Add(value); Current = RefuseReadback ? value + 1 : value; AfterWrite?.Invoke(); return true; }
    }
}
