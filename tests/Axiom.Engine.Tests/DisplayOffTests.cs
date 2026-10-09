using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

// Synthetic only: automated validation must never send a real SC_MONITORPOWER request.
public sealed class DisplayOffTests
{
    private static TaskInvocation Invocation(string delay = "0") => new(DisplayOffCapabilities.Request, 1,
        new Dictionary<string, string> { ["delaySeconds"] = delay }.ToImmutableDictionary());

    [Fact]
    public void ActualRegistryTemplateCompileAndImportUseOneRequestWithStrictParameters()
    {
        var modules = BuiltInAutomationPlugins.Create();
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var plugin = Assert.Single(modules.OfType<DisplayOffPlugin>()); var binding = Assert.Single(plugin.Bindings).Descriptor;
        Assert.Equal(ConfirmationSource.ReceiptOnly, binding.Confirmation); Assert.Equal(RetryCategory.Never, binding.Retry);
        Assert.Contains(plugin.Combinations, c => c.DetectionId == HotkeyCapabilities.Pressed);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "lock-display-off");
        Assert.True(template.Draft.Paused); Assert.Equal("locked", template.Draft.Detection.Parameters["event"]);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        Assert.NotNull(compiler.Compile(template.Draft, "owner", ResourceScope.Session("owner", 1)));
        var action = new DisplayOffTaskAction(); Assert.True(action.SupportsTimeWindow);
        Assert.Equal("current-session-display-off", action.Compile(Invocation(), ResourceScope.Session("owner", 1)).Key.ResourceId);
        Assert.Equal(TimeSpan.FromSeconds(60), action.Timing(Invocation("60")).MinimumDuration);
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "锁屏关闭屏幕", "合成建议", template.Draft.Detection, Invocation());
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        foreach (var invalid in new[] { "-1", "86401", "0.5", " 1" })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = Invocation(invalid) }), recorder.Session, catalog));
        foreach (var extra in new[] { "sessionId", "restore", "retry", "keepOff" })
            Assert.Throws<ArgumentException>(() => DisplayOffCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add(extra, "1") }));
    }

    [Fact]
    public void ActiveLockedAndUnlockedSessionsSendOnceWhileUnknownInactiveAndNativeRefusalDoNotClaimSuccess()
    {
        foreach (var state in new[] { SessionLockState.Locked, SessionLockState.Unlocked })
        {
            var api = new FakeApi { State = state }; var result = DisplayOffOperation.Apply(api, default);
            Assert.True(result.Success); Assert.Contains("Windows已接受一次", result.Reason); Assert.Contains("是否关闭未确认", result.Reason);
            Assert.Equal(1, api.Reads); Assert.Equal(1, api.Requests); Assert.Equal(state, api.State);
        }
        foreach (var state in new[] { SessionLockState.Unknown, SessionLockState.NonInteractive })
        {
            var api = new FakeApi { State = state }; Assert.False(DisplayOffOperation.Apply(api, default).Success);
            Assert.Equal(1, api.Reads); Assert.Equal(0, api.Requests);
        }
        foreach (var error in new[] { 0, 5, 1460 })
        {
            var api = new FakeApi { Accepted = false, Error = error }; var result = DisplayOffOperation.Apply(api, default);
            Assert.False(result.Success); Assert.Contains("不自动重试", result.Reason); Assert.Equal(1, api.Requests);
        }
    }

    [Fact]
    public void CancellationPreventsUnsentRequestButCannotReverseOneAlreadySent()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new FakeApi();
        Assert.ThrowsAny<OperationCanceledException>(() => DisplayOffOperation.Apply(untouched, before.Token));
        Assert.Equal(0, untouched.Reads); Assert.Equal(0, untouched.Requests);
        using var reading = new CancellationTokenSource(); var canceled = new FakeApi { AfterRead = reading.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => DisplayOffOperation.Apply(canceled, reading.Token)); Assert.Equal(0, canceled.Requests);
        using var after = new CancellationTokenSource(); var requested = new FakeApi { AfterRequest = after.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => DisplayOffOperation.Apply(requested, after.Token)); Assert.Equal(1, requested.Requests);
    }

    [Fact(Timeout = 10000)]
    public async Task CanceledBlockedRequestKeepsNativeAdmissionAndDrainUntilReturn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var commands = new BoundedNativeCommand("关闭屏幕请求", TimeSpan.FromSeconds(5));
        var api = new FakeApi { AfterRequest = () => { entered.SetResult(); release.Wait(); } };
        var request = commands.RunAsync(token => DisplayOffOperation.Apply(api, token), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(commands.DrainAsync().IsCompleted);
            Assert.False((await commands.RunAsync(token => DisplayOffOperation.Apply(api, token), default)).Success);
            Assert.Equal(1, api.Requests);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(1, api.Reads); Assert.Equal(1, api.Requests);
    }

    private sealed class FakeApi : IDisplayOffApi
    {
        internal SessionLockState State = SessionLockState.Unlocked;
        internal bool Accepted = true; internal int Error, Reads, Requests;
        internal Action? AfterRead, AfterRequest;
        public SessionLockState GetCurrentState() { Reads++; AfterRead?.Invoke(); return State; }
        public (bool Accepted, int Error) RequestDisplayOff() { Requests++; AfterRequest?.Invoke(); return (Accepted, Error); }
    }
}
