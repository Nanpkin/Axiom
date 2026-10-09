using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

// Synthetic APIs only: never call WindowsSystemSound.RequestAsync during automated validation.
public sealed class SystemSoundTests
{
    private static TaskInvocation Invocation(string sound = "default", string delay = "0") => new(SystemSoundCapabilities.Sound, 1,
        ImmutableDictionary<string, string>.Empty.Add("sound", sound).Add("delaySeconds", delay));

    [Fact]
    public void ActualRegistryPausedWorkdayTemplateAndStrictImportOnlyAllowDocumentedSystemSounds()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<SystemSoundPlugin>());
        Assert.Equal(ConfirmationSource.ReceiptOnly, Assert.Single(plugin.Bindings).Descriptor.Confirmation);
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "workday-system-sound");
        Assert.True(template.Draft.Paused); Assert.Equal("default", template.Draft.Action.Parameters["sound"]);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        var scope = ResourceScope.Session("owner", 1); Assert.NotNull(compiler.Compile(template.Draft, "owner", scope));
        var action = new SystemSoundTaskAction(); Assert.True(action.SupportsTimeWindow);
        Assert.Equal("system-sound", action.Compile(Invocation(), scope).Key.ResourceId);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("warning"), scope).Key);
        Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation(delay: "12")).MinimumDuration);
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        string Proposal(TaskInvocation command) => JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id, "existing", "系统提示音", "合成审阅", template.Draft.Detection, command));
        Assert.True(UsageAnalysis.Import(Proposal(Invocation()), recorder.Session, catalog).Draft!.Paused);
        foreach (var invalid in new[] { Invocation("file"), Invocation("https://example.invalid/tone"), Invocation(delay: "-1"), Invocation(delay: "1.5"), Invocation(delay: "86401") })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(invalid), recorder.Session, catalog));
        foreach (var extra in new[] { "file", "url", "tts", "volume", "loop", "unmute" })
            Assert.Throws<ArgumentException>(() => SystemSoundCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add(extra, "1") }));
    }

    [Fact]
    public void KnownSoundsRequestOnceAndSuccessMeansAcceptedWhileRefusalOrFailureNeverRetries()
    {
        foreach (var (sound, type) in new[] { ("default", 0u), ("info", 0x40u), ("warning", 0x30u), ("error", 0x10u) })
        {
            var api = new Api(); var result = SystemSoundOperation.Apply(sound, api, default);
            Assert.True(result.Success); Assert.Equal(1, api.Requests); Assert.Equal(type, api.Type);
            Assert.Contains("已接受一次", result.Reason); Assert.Contains("不保证听见", result.Reason);
        }
        var refused = new Api { Accepted = false }; Assert.False(SystemSoundOperation.Apply("default", refused, default).Success); Assert.Equal(1, refused.Requests);
        var unavailable = new Api { AfterRequest = () => throw new UnauthorizedAccessException() };
        var failure = SystemSoundOperation.Apply("warning", unavailable, default);
        Assert.False(failure.Success); Assert.Contains("不自动重试", failure.Reason); Assert.Equal(1, unavailable.Requests);
        var untouched = new Api(); Assert.Throws<ArgumentException>(() => SystemSoundOperation.Apply("unsupported", untouched, default)); Assert.Equal(0, untouched.Requests);
    }

    [Fact]
    public void CancellationBeforeRequestPreventsSoundButAfterAcceptedRequestCannotRetractIt()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var untouched = new Api();
        Assert.ThrowsAny<OperationCanceledException>(() => SystemSoundOperation.Apply("default", untouched, before.Token)); Assert.Equal(0, untouched.Requests);
        using var after = new CancellationTokenSource(); var accepted = new Api { AfterRequest = after.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => SystemSoundOperation.Apply("default", accepted, after.Token)); Assert.Equal(1, accepted.Requests);
    }

    [Fact(Timeout = 10000)]
    public async Task CanceledBlockedSoundRequestKeepsAdmissionAndShutdownDrainUntilNativeReturn()
    {
        var commands = new BoundedNativeCommand("系统提示音", TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var stop = new CancellationTokenSource();
        var api = new Api { AfterRequest = () => { entered.SetResult(); release.Wait(); } };
        var request = commands.RunAsync(token => SystemSoundOperation.Apply("default", api, token), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(commands.DrainAsync().IsCompleted);
            Assert.False((await commands.RunAsync(token => SystemSoundOperation.Apply("default", api, token), default)).Success);
            Assert.Equal(1, api.Requests);
        }
        finally { release.Set(); await commands.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Equal(1, api.Requests);
    }

    private sealed class Api : ISystemSoundApi
    {
        internal bool Accepted = true; internal int Requests; internal uint Type; internal Action? AfterRequest;
        public bool Request(uint soundType) { Requests++; Type = soundType; AfterRequest?.Invoke(); return Accepted; }
    }
}
