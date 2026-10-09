using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class UsbDeviceTests
{
    private const string First = @"USB\VID_1234&PID_5678\SYNTHETIC_A";
    private const string Second = @"USB\VID_1234&PID_5678\SYNTHETIC_B";
    private static TaskInvocation Detection(bool absent = false, string? ids = null) => new(absent ? UsbDeviceCapabilities.Absent : UsbDeviceCapabilities.Present, 1,
        ImmutableDictionary<string, string>.Empty.Add("deviceInstanceIds", ids ?? First + "\n" + Second));

    [Fact]
    public void OnlyFullUsbInstanceIdsAreAcceptedAndResourceIdentityIsCaseInsensitive()
    {
        Assert.Equal(new[] { First, Second }, UsbDeviceCapabilities.Parse(First.ToLowerInvariant() + "\r\n" + First + "\n" + Second));
        foreach (var value in new[] { "", "VID_1234&PID_5678", @"USB\VID_1234&PID_5678", @"USB\VID_1234&PID_5678\", @"HID\VID_1234&PID_5678\SYNTHETIC_A",
            @"USBSTOR\DISK\SYNTHETIC_A", @"\\?\USB#VID_1234&PID_5678#SYNTHETIC_A#{synthetic}", First + "*", First + "?", First + "\0", First + "\t", First + "\n\n ",
            @"USB\VID_1234\" + new string('x', 255), string.Join('\n', Enumerable.Range(0, 17).Select(i => First + i)) })
            Assert.Throws<ArgumentException>(() => UsbDeviceCapabilities.Parse(value));
        var compiler = new UsbDeviceTaskDetection(false);
        var lower = compiler.Compile(Detection(ids: First.ToLowerInvariant()), ResourceScope.Machine);
        Assert.Equal(First, Assert.Single(lower.Dependencies).ResourceId);
        Assert.Throws<ArgumentException>(() => compiler.Compile(Detection() with { Parameters = Detection().Parameters.Add("hardwareId", "VID_1234") }, ResourceScope.Machine));
        Assert.Throws<ArgumentException>(() => compiler.Compile(Detection() with { SchemaVersion = 2 }, ResourceScope.Machine));
    }

    [Fact]
    public void ExactInstanceMatchingDistinguishesIdenticalModelsAndEmptyFromFailedQueries()
    {
        var state = new UsbDeviceState([First, Second]); Assert.All(state.Samples(), s => Assert.Null(s.Present));
        state.Replace([]); Assert.All(state.Samples(), s => Assert.False(s.Present));
        state.Replace([First.ToLowerInvariant(), First]); Assert.True(state.Samples()[0].Present); Assert.False(state.Samples()[1].Present);
        state.Replace([First + "_OTHER"]); Assert.All(state.Samples(), s => Assert.False(s.Present));
        state.Replace([First, Second]); state.Replace([Second]); Assert.Equal(Second, state.Samples()[0].InstanceId);
        Assert.True(state.Samples()[0].Present); Assert.False(state.Samples()[1].Present);
        state.Replace(null); Assert.All(state.Samples(), s => Assert.Null(s.Present));
        Assert.Throws<IOException>(() => state.Replace(Enumerable.Range(0, 257).Select(i => First + i).ToArray()));
        Assert.Throws<IOException>(() => state.Replace(["bad\0instance"])); Assert.All(state.Samples(), s => Assert.Null(s.Present));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyPresenceAndAllAbsencePropagateUnknownAndOfferKeepAwakeOnlyForPresence(bool absent)
    {
        var condition = new UsbDeviceTaskDetection(absent).Compile(Detection(absent), ResourceScope.Machine);
        Assert.Equal(absent ? ConditionKind.All : ConditionKind.Any, condition.Kind);
        Assert.All(condition.Dependencies, k => Assert.Equal(UsbDeviceCapabilities.Present, k.CapabilityId));
        var source = new SourceSession("synthetic-usb", 1); var schema = new ValueSchema("axiom.bool", 1);
        TruthValue Evaluate(bool? a, bool? b) => RuleEvaluator.Evaluate(condition, condition.Dependencies.ToDictionary(k => k, k =>
        {
            var value = k.ResourceId == First ? a : b;
            return new ObservedState(new(k, value.HasValue ? StateQuality.Known : StateQuality.Unknown, value.HasValue ? StateValue.From(schema, value.Value) : null,
                DateTimeOffset.UnixEpoch), source, 1, 1);
        })).Value;
        Assert.Equal(absent ? TruthValue.False : TruthValue.True, Evaluate(true, false));
        Assert.Equal(absent ? TruthValue.True : TruthValue.False, Evaluate(false, false));
        Assert.Equal(TruthValue.Unknown, Evaluate(false, null));
        Assert.Equal(absent ? TruthValue.False : TruthValue.True, Evaluate(true, null));
        var combinations = new UsbDevicePlugin().Combinations;
        Assert.Contains(new(UsbDeviceCapabilities.Present, KeepAwakeCapabilities.Hold), combinations);
        Assert.DoesNotContain(new(UsbDeviceCapabilities.Absent, KeepAwakeCapabilities.Hold), combinations);
        Assert.DoesNotContain(combinations, c => c.ActionId == HotkeyCapabilities.Send);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineTriggersOnlyNewGroupEdgesAndNeverReplaysInitialResumedOrPausedState(bool absent)
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(UsbDeviceCapabilities.Present, schema: schema));
        var condition = new UsbDeviceTaskDetection(absent).Compile(Detection(absent), ResourceScope.Machine);
        var keys = condition.Dependencies.ToDictionary(k => k.ResourceId);
        var source = fixture.Engine.RegisterSource("synthetic-usb", keys.Values); long sequence = 0;
        var state = new UsbDeviceState(keys.Keys.ToArray());
        var rule = new RuleDefinition("synthetic-usb-rule", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Observation Observation(UsbDeviceValue value) => new(keys[value.InstanceId], value.Present.HasValue ? StateQuality.Known : StateQuality.Unknown,
            value.Present.HasValue ? StateValue.From(schema, value.Present.Value) : null, fixture.Clock.UtcNow);
        void Set(string[]? current, bool snapshot = false)
        {
            state.Replace(current); var values = state.Samples().Select(Observation).ToArray();
            if (snapshot) fixture.Engine.ApplySnapshot(source, ++sequence, values);
            else foreach (var value in values) fixture.Engine.Update(source, ++sequence, value);
        }
        Set(absent ? [] : [First], true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution();
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set([First, Second]); Set([Second]); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set([]); if (absent) { (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); }
        Set([First]); if (!absent) { (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); }
        Assert.Equal(1, executor.Count);
        Set(null); Set(absent ? [] : [First], true); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", ResourceScope.Machine, condition, rule.Action, enabled: false));
        Set([]); Set([First]); Set(absent ? [] : [First]);
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", ResourceScope.Machine, condition, rule.Action));
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(absent ? [Second] : []); Set(absent ? [] : [First]);
        (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task NotificationBatchesAreBoundedAndQueriesRunOnlyOnRelevantHints()
    {
        var hints = Channel.CreateBounded<string>(128); var updates = Channel.CreateBounded<(UsbDeviceValue[] Values, bool Snapshot)>(8);
        using var lifetime = new CancellationTokenSource(); var queries = 0;
        Assert.True(hints.Writer.TryWrite("locked"));
        for (var i = 0; i < 64; i++) Assert.True(hints.Writer.TryWrite("usb-arrived"));
        var run = UsbDeviceObservation.RunAsync([First], hints.Reader, () => ++queries == 1 ? [] : [First], (samples, baseline) =>
            Assert.True(updates.Writer.TryWrite((samples, baseline))), lifetime.Token);
        var initial = await updates.Reader.ReadAsync(); Assert.True(initial.Snapshot); Assert.False(initial.Values[0].Present);
        var changed = await updates.Reader.ReadAsync(); Assert.False(changed.Snapshot); Assert.True(changed.Values[0].Present); Assert.Equal(2, queries);
        Assert.False(updates.Reader.TryRead(out _));
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, queries); // cancellation does not perform a cleanup scan
    }

    [Fact(Timeout = 10000)]
    public async Task QueryFaultAndCapacityBecomeUnknownAndRecoveryOrResumeIsABaseline()
    {
        var hints = Channel.CreateBounded<string>(64); var updates = Channel.CreateBounded<(UsbDeviceValue[] Values, bool Snapshot)>(8);
        using var lifetime = new CancellationTokenSource(); string[] current = [First]; Exception? failure = null;
        var run = UsbDeviceObservation.RunAsync([First], hints.Reader, () => failure is null ? current : throw failure,
            (samples, baseline) => Assert.True(updates.Writer.TryWrite((samples, baseline))), lifetime.Token);
        Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        failure = new IOException("synthetic query failure"); hints.Writer.TryWrite("usb-removed");
        var unknown = await updates.Reader.ReadAsync(); Assert.False(unknown.Snapshot); Assert.Null(unknown.Values[0].Present);
        failure = null; current = []; hints.Writer.TryWrite("usb-removed");
        var recovered = await updates.Reader.ReadAsync(); Assert.True(recovered.Snapshot); Assert.False(recovered.Values[0].Present);
        current = [First]; hints.Writer.TryWrite("resumed");
        var resumed = await updates.Reader.ReadAsync(); Assert.True(resumed.Snapshot); Assert.True(resumed.Values[0].Present);
        current = Enumerable.Range(0, 257).Select(i => First + i).ToArray(); hints.Writer.TryWrite("usb-arrived");
        Assert.Null((await updates.Reader.ReadAsync()).Values[0].Present);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact(Timeout = 10000)]
    public async Task LostNotificationsSuppressQueryPublicationAndEndTheSource()
    {
        var hints = Channel.CreateBounded<string>(64); var updates = new List<UsbDeviceValue[]>(); var queries = 0; var failed = false;
        using var lifetime = new CancellationTokenSource();
        var run = UsbDeviceObservation.RunAsync([First], hints.Reader, () =>
        {
            queries++; if (queries == 2) failed = true; return [First];
        }, (samples, _) => updates.Add(samples), lifetime.Token, () => failed);
        Assert.Single(updates); hints.Writer.TryWrite("usb-removed");
        await Assert.ThrowsAsync<IOException>(() => run); Assert.Single(updates); Assert.Equal(2, queries);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationFailurePublishesUnknownWithoutEndingOtherProviders(bool afterSnapshot)
    {
        var updates = Channel.CreateBounded<ProviderUpdate>(4); using var lifetime = new CancellationTokenSource();
        var provider = new UsbDeviceCapabilityProvider((ids, publish, token) =>
        {
            if (afterSnapshot) publish([new(ids[0], true)], true);
            throw new IOException("synthetic native failure");
        });
        var run = provider.ObserveAsync([new(UsbDeviceCapabilities.Present, First)], u => Assert.True(updates.Writer.TryWrite(u)), lifetime.Token);
        if (afterSnapshot) Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        var update = await updates.Reader.ReadAsync(); Assert.Equal(!afterSnapshot, update.Snapshot);
        var value = Assert.Single(update.Samples); Assert.Equal(StateQuality.Unknown, value.Quality); Assert.Null(value.Boolean);
        Assert.Contains("USB", value.Reason); Assert.False(run.IsCompleted);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task LocalIdsSurviveStorageButCatalogAndAiImportNeverRevealOrSupplyThem()
    {
        var plugin = new UsbDevicePlugin(); var compiler = new AutomationTaskCompiler();
        foreach (var detection in plugin.Detections) compiler.Register(detection); compiler.Register(new KeepAwakeTaskAction());
        var action = new TaskInvocation(KeepAwakeCapabilities.Hold, 1, ImmutableDictionary<string, string>.Empty.Add("target", "system"));
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic USB power", true, Detection(), action);
        var path = Path.Combine(Path.GetTempPath(), "axiom-usb-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(task, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(task.Detection.Parameters["deviceInstanceIds"], loaded.Detection.Parameters["deviceInstanceIds"]);
            Assert.Equal(RuleActionKind.Desired, compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
            var catalog = new AutomationSnapshot([loaded], compiler.Catalog, plugin.Combinations, true, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, []);
            Assert.DoesNotContain("SYNTHETIC_A", UsageAnalysis.CapabilityCatalog(catalog, false));
            var localDefaults = catalog with { Capabilities = [.. catalog.Capabilities.Select(c => c.Id == UsbDeviceCapabilities.Present ? c with
                { Fields = [.. c.Fields.Select(f => f with { DefaultValue = First, Choices = [new(First, First)] })] } : c)] };
            Assert.DoesNotContain("SYNTHETIC_A", UsageAnalysis.CapabilityCatalog(localDefaults, true));
            var recorder = new UsageRecorder(new(5, true, false, []), DateTimeOffset.UnixEpoch);
            var proposal = new UsageImport(1, recorder.Session.Id, "existing", "synthetic suggestion", "synthetic evidence", Detection(ids: ""), action);
            var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
            Assert.True(draft.Paused); Assert.Contains("USB 设备实例路径", UsageAnalysis.MissingManualFields(draft, catalog));
            Assert.Throws<ArgumentException>(() => compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection() }), recorder.Session, catalog));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }
}
