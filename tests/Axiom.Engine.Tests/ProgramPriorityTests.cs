using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class ProgramPriorityTests
{
    private static string App(string directory = "selected") => Path.Combine(Path.GetTempPath(), directory, "Tool.exe");
    private static TaskInvocation Invocation(string priority = "below-normal") => new(ProgramPriorityCapabilities.Set, 1,
        new Dictionary<string, string> { ["executable"] = App(), ["priority"] = priority, ["delaySeconds"] = "12" }.ToImmutableDictionary());

    [Fact]
    public void RegistryAndImportUseFullPathsAndOnlyThreePrioritiesWithStrictParameters()
    {
        var action = new ProgramPriorityTaskAction(); var scope = ResourceScope.Session("owner", 1);
        Assert.True(action.SupportsTimeWindow); Assert.Equal(TimeSpan.FromSeconds(12), action.Timing(Invocation()).MinimumDuration);
        Assert.Equal(TaskFieldKind.ExecutableSelection, action.Descriptor.Fields.Single(f => f.Id == "executable").Kind);
        Assert.Equal(action.Compile(Invocation(), scope).Key, action.Compile(Invocation("normal"), scope).Key);
        Assert.NotEqual(action.Compile(Invocation(), scope).Key, action.Compile(Invocation() with { Parameters = Invocation().Parameters.SetItem("executable", App("other")) }, scope).Key);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<ProgramPriorityPlugin>());
        Assert.Contains(plugin.Combinations, c => c.DetectionId == "process.running" && c.ActionId == ProgramPriorityCapabilities.Set);
        Assert.Equal(RetryCategory.Never, Assert.Single(plugin.Bindings).Descriptor.Retry);
        foreach (var invalid in new[] { "high", "realtime", "idle", "NORMAL" })
            Assert.Throws<ArgumentException>(() => ProgramPriorityCapabilities.Validate(Invocation(invalid)));
        Assert.Throws<ArgumentException>(() => ProgramPriorityCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.Add("command", "ignored") }));
        Assert.Throws<ArgumentException>(() => ProgramPriorityCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem("executable", "Tool.exe") }));
        Assert.Throws<ArgumentException>(() => ProgramPriorityCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem("delaySeconds", "86401") }));

        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        recorder.Add(new(DateTimeOffset.UtcNow, "process-start", App()));
        var modules = BuiltInAutomationPlugins.Create();
        var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var alias = recorder.Session.Objects.Single().Id;
        var detection = new TaskInvocation("process.running", 2, new Dictionary<string, string> { ["processNames"] = alias }.ToImmutableDictionary());
        var imported = Invocation() with { Parameters = Invocation().Parameters.SetItem("executable", alias) };
        string Proposal(TaskInvocation command) => JsonSerializer.Serialize(new UsageImport(1, recorder.Session.Id, "existing", "priority", "review", detection, command));
        var draft = UsageAnalysis.Import(Proposal(imported), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(App(), draft.Action.Parameters["executable"]);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(imported with { Parameters = imported.Parameters.SetItem("priority", "realtime") }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Proposal(Invocation()), recorder.Session, catalog));
    }

    [Fact]
    public void ThreeClassesChangeAllCurrentMatchesOnceAndReadBackWithoutTouchingOtherIdentityOrPath()
    {
        foreach (var (priority, expected) in new[] { ("below-normal", 0x4000u), ("normal", 0x20u), ("above-normal", 0x8000u) })
        {
            var first = new Instance(App()); var second = new Instance(App()); var otherPath = new Instance(App("other"));
            var otherOwnerOrSession = new Instance(App()) { Identity = PriorityProcessIdentity.Other };
            var exited = new Instance(App()) { Identity = PriorityProcessIdentity.Missing };
            var result = ProgramPriorityOperation.Apply([first, second, otherPath, otherOwnerOrSession, exited], [App()], priority, default);
            Assert.True(result.Success); Assert.Contains("确认2个实例", result.Reason);
            foreach (var match in new[] { first, second }) { Assert.Equal(expected, match.Value); Assert.Equal(1, match.Writes); Assert.Equal(2, match.Reads); }
            foreach (var ignored in new[] { otherPath, otherOwnerOrSession, exited }) Assert.Equal(0, ignored.Writes);
            Assert.True(ProgramPriorityOperation.Apply([first], [App()], priority, default).Success); Assert.Equal(1, first.Writes);
        }
    }

    [Fact]
    public void MissingDeniedChangedAndUnconfirmedInstancesProducePartialFailureWithoutRetryOrRestore()
    {
        var good = new Instance(App()); var changed = new Instance(App()) { ChangeIdentity = true };
        var denied = new Instance(App()) { Deny = true }; var unknown = new Instance(App()) { Identity = PriorityProcessIdentity.Unknown };
        var readback = new Instance(App()) { Mismatch = true };
        var result = ProgramPriorityOperation.Apply([good, changed, denied, unknown, readback], [App(), App("missing")], "below-normal", default);
        Assert.False(result.Success); Assert.Contains("部分完成", result.Reason); Assert.Contains("权限不足", result.Reason);
        Assert.Contains("身份无法核对", result.Reason); Assert.Contains("未发现可核验的运行实例", result.Reason);
        Assert.Equal(0, changed.Writes); Assert.Equal(0, unknown.Writes); Assert.Equal(1, denied.Writes);
        Assert.Equal(1, readback.Writes); Assert.Equal(0x4000u, readback.Value); Assert.Equal(1, good.Writes);
        Assert.False(ProgramPriorityOperation.Apply([], [App()], "normal", default).Success);
    }

    [Fact]
    public async Task CancellationKeepsEarlierWriteAndBoundedCommandRetainsAdmissionUntilNativeWorkerEnds()
    {
        using var stop = new CancellationTokenSource(); var first = new Instance(App()) { AfterWrite = stop.Cancel }; var later = new Instance(App());
        Assert.ThrowsAny<OperationCanceledException>(() => ProgramPriorityOperation.Apply([first, later], [App()], "below-normal", stop.Token));
        Assert.Equal(0x4000u, first.Value); Assert.Equal(1, first.Writes); Assert.Equal(0, later.Writes);
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(); var command = new BoundedNativeCommand("合成优先级", TimeSpan.FromSeconds(5));
        var work = command.RunAsync(_ => { started.Set(); release.Wait(); return (true, "done"); }, cancellation.Token);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5))); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.False((await command.RunAsync(_ => throw new InvalidOperationException("must not start"), default)).Success);
        }
        finally { release.Set(); await command.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class Instance(string path) : IPriorityProcess
    {
        internal PriorityProcessIdentity Identity = PriorityProcessIdentity.Owned;
        internal bool ChangeIdentity, Deny, Mismatch; internal Action? AfterWrite;
        internal int Writes, Reads, Checks; internal uint Value = 0x80;
        public PriorityProcessIdentity Identify(out string? value) { value = path; return ChangeIdentity && ++Checks > 1 ? PriorityProcessIdentity.Unknown : Identity; }
        public uint GetPriority() { Reads++; return Mismatch && Writes > 0 ? 0x20u : Value; }
        public void SetPriority(uint value) { Writes++; if (Deny) throw new UnauthorizedAccessException(); Value = value; AfterWrite?.Invoke(); }
    }
}
