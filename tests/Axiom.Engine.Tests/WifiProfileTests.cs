using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class WifiProfileTests
{
    private static TaskInvocation Detection(bool disconnected = false, string profiles = "Synthetic Office\nSynthetic Home") => new(
        disconnected ? WifiProfileCapabilities.Disconnected : WifiProfileCapabilities.Connected, 1,
        ImmutableDictionary<string, string>.Empty.Add("profileNames", profiles));
    private static TaskInvocation Reminder() => new(NotificationCapabilities.Show, 1,
        new Dictionary<string, string> { ["title"] = "synthetic reminder", ["body"] = "synthetic body", ["sound"] = "false", ["delaySeconds"] = "0" }.ToImmutableDictionary());

    [Fact]
    public void ProfileSelectionPreservesExactNamesAndBoundsLocalInput()
    {
        Assert.Equal(new[] { "Synthetic Office", " synthetic ", "synthetic office" }, WifiProfileCapabilities.Parse("Synthetic Office\r\n synthetic \nSynthetic Office\nsynthetic office\n"));
        foreach (var value in new[] { "", " \n", "synthetic\0name", "synthetic\tname", "synthetic\rname", new string('a', 256),
            string.Join('\n', Enumerable.Range(0, 17).Select(i => "Synthetic " + i)), new string('a', 4097) })
            Assert.Throws<ArgumentException>(() => WifiProfileCapabilities.Parse(value));
        Assert.Equal(16, WifiProfileCapabilities.Parse(string.Join('\n', Enumerable.Range(0, 16).Select(i => i.ToString().PadRight(255, 'x')))).Length);
        var compiler = new WifiProfileTaskDetection(false);
        Assert.Throws<ArgumentException>(() => compiler.Compile(Detection() with { Parameters = Detection().Parameters.Add("ssid", "synthetic") }, ResourceScope.Machine));
        Assert.Throws<ArgumentException>(() => compiler.Compile(Detection() with { SchemaVersion = 2 }, ResourceScope.Machine));
    }

    [Fact]
    public void MultipleInterfacesRequireConfirmedAbsenceButKnownConnectionWinsOverUnrelatedFailure()
    {
        var state = new WifiProfileState(["Synthetic Office", "Synthetic Home"]);
        Assert.All(state.Samples(), v => Assert.Null(v.Connected)); // no hardware
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        state.Replace([new(first, true, "Synthetic Office"), new(second, false, null)]);
        Assert.True(state.Samples()[0].Connected); Assert.Null(state.Samples()[1].Connected);
        state.Set(new(second, true, "Synthetic Office")); state.Set(new(first, true, null));
        Assert.True(state.Samples().Single(v => v.ProfileName == "Synthetic Office").Connected);
        state.Set(new(second, true, "synthetic office")); // different profile identity
        Assert.All(state.Samples(), v => Assert.False(v.Connected));
        state.Set(new(first, false, null)); Assert.All(state.Samples(), v => Assert.Null(v.Connected));
        state.Replace([new(second, true, null)]); Assert.All(state.Samples(), v => Assert.False(v.Connected));
        state.Replace([]); Assert.All(state.Samples(), v => Assert.Null(v.Connected));
    }

    [Fact]
    public void NativeInterfaceStateMustProveDisconnectionAndAdHocFormationIsUnknown()
    {
        Assert.True(WifiProfileState.ConnectionStatus(1));
        foreach (var value in new uint[] { 0, 2, 3, 8, uint.MaxValue }) Assert.Null(WifiProfileState.ConnectionStatus(value));
        foreach (var value in new uint[] { 4, 5, 6, 7 }) Assert.False(WifiProfileState.ConnectionStatus(value));
    }

    [Fact]
    public void AdapterCapacityFailsBeforeDiscardingExistingKnownState()
    {
        var state = new WifiProfileState(["Synthetic Office"]);
        var values = Enumerable.Range(0, 64).Select(_ => new WifiInterfaceValue(Guid.NewGuid(), true, null)).ToArray();
        state.Replace(values); Assert.False(state.Samples()[0].Connected);
        Assert.Throws<IOException>(() => state.Set(new(Guid.NewGuid(), true, null)));
        Assert.Throws<IOException>(() => state.Replace([.. values, new(Guid.NewGuid(), true, null)]));
        Assert.Throws<IOException>(() => state.Replace([values[0], values[0]]));
        Assert.False(state.Samples()[0].Connected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompilesAnyConnectedOrAllDisconnectedAndPropagatesUnknown(bool disconnected)
    {
        var condition = new WifiProfileTaskDetection(disconnected).Compile(Detection(disconnected), ResourceScope.Machine);
        Assert.Equal(disconnected ? ConditionKind.All : ConditionKind.Any, condition.Kind);
        Assert.All(condition.Dependencies, key => Assert.Equal(WifiProfileCapabilities.Connected, key.CapabilityId));
        var source = new SourceSession("synthetic-wlan", 1);
        var schema = new ValueSchema("axiom.bool", 1);
        TruthValue Evaluate(bool? a, bool? b)
        {
            var states = condition.Dependencies.ToDictionary(k => k, k =>
            {
                var value = k.ResourceId == "Synthetic Office" ? a : b;
                return new ObservedState(new(k, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                    value.HasValue ? StateValue.From(schema, value.Value) : null, DateTimeOffset.UnixEpoch), source, 1, 1);
            });
            return RuleEvaluator.Evaluate(condition, states).Value;
        }
        Assert.Equal(disconnected ? TruthValue.False : TruthValue.True, Evaluate(true, false));
        Assert.Equal(disconnected ? TruthValue.True : TruthValue.False, Evaluate(false, false));
        Assert.Equal(TruthValue.Unknown, Evaluate(false, null));
        Assert.Equal(disconnected ? TruthValue.False : TruthValue.True, Evaluate(true, null));
        Assert.DoesNotContain(new WifiProfilePlugin().Combinations, c => c.ActionId == HotkeyCapabilities.Send);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewEdgesExecuteOnceWhileSwitchingInitialReconnectAndPauseNeverReplay(bool disconnected)
    {
        var executor = new ControlledExecutor(); using var fixture = new AutomationFixture(executor);
        var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(WifiProfileCapabilities.Connected, schema: schema));
        var condition = new WifiProfileTaskDetection(disconnected).Compile(Detection(disconnected), ResourceScope.Machine);
        var keys = condition.Dependencies.ToDictionary(k => k.ResourceId);
        var source = fixture.Engine.RegisterSource("synthetic-wlan", keys.Values); long sequence = 0;
        var state = new WifiProfileState(keys.Keys.ToArray()); var adapter = Guid.NewGuid();
        var rule = new RuleDefinition("synthetic-wifi-rule", 1, "owner", ResourceScope.Machine, condition,
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Observation Observation(WifiProfileValue value) => new(keys[value.ProfileName], value.Connected.HasValue ? StateQuality.Known : StateQuality.Unknown,
            value.Connected.HasValue ? StateValue.From(schema, value.Connected.Value) : null, fixture.Clock.UtcNow);
        void Set(string? profile, bool snapshot = false, bool known = true)
        {
            state.Replace([new(adapter, known, profile)]); var samples = state.Samples();
            if (snapshot) fixture.Engine.ApplySnapshot(source, ++sequence, samples.Select(Observation).ToArray());
            else foreach (var sample in samples) fixture.Engine.Update(source, ++sequence, Observation(sample));
        }
        Set(disconnected ? null : "Synthetic Office", true);
        fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set("Synthetic Office"); Set("Synthetic Home"); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        Set(null);
        if (disconnected) { (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); }
        Set("Synthetic Office");
        if (!disconnected) { (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); }
        Assert.Equal(1, executor.Count);
        Set(null, known: false); Set(disconnected ? null : "Synthetic Office", true);
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        fixture.Engine.SetRule(new(rule.Id, 2, "owner", ResourceScope.Machine, condition, rule.Action, enabled: false));
        Set(null); Set("Synthetic Office"); Set(disconnected ? null : "Synthetic Office");
        fixture.Engine.SetRule(new(rule.Id, 3, "owner", ResourceScope.Machine, condition, rule.Action));
        await fixture.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        Set(disconnected ? "Synthetic Home" : null); Set(disconnected ? null : "Synthetic Office");
        (await executor.NextAsync()).Complete(); await fixture.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureIsUnknownAndIsolatedUntilCancelled(bool afterSnapshot)
    {
        var updates = Channel.CreateBounded<ProviderUpdate>(4); using var lifetime = new CancellationTokenSource();
        var provider = new WifiProfileCapabilityProvider((profiles, publish, token) =>
        {
            if (afterSnapshot) publish([new(profiles[0], true)], true);
            throw new IOException("synthetic service failure");
        });
        var run = provider.ObserveAsync([new(WifiProfileCapabilities.Connected, "Synthetic Office")], u => Assert.True(updates.Writer.TryWrite(u)), lifetime.Token);
        if (afterSnapshot) Assert.True((await updates.Reader.ReadAsync()).Snapshot);
        var update = await updates.Reader.ReadAsync(); Assert.Equal(!afterSnapshot, update.Snapshot);
        var value = Assert.Single(update.Samples); Assert.Equal(StateQuality.Unknown, value.Quality); Assert.Null(value.Boolean);
        Assert.Contains("WLAN", value.Reason); Assert.False(run.IsCompleted);
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task ConfigurationSurvivesStorageAndAnonymousImportRequiresManualProfileNames()
    {
        var plugin = new WifiProfilePlugin(); var compiler = new AutomationTaskCompiler();
        foreach (var detection in plugin.Detections) compiler.Register(detection);
        compiler.Register(new NotificationTaskAction());
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic Wi-Fi reminder", true, Detection(), Reminder());
        var path = Path.Combine(Path.GetTempPath(), "axiom-wifi-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(task, default);
            using var reopened = new SqliteAutomationTaskStore(path); var saved = Assert.Single(await reopened.ListAsync());
            Assert.Equal(task.Detection.Parameters["profileNames"], saved.Detection.Parameters["profileNames"]);
            Assert.Equal(RuleActionKind.Command, compiler.Compile(saved, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
            var catalog = new AutomationSnapshot([saved], compiler.Catalog, plugin.Combinations, true, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, []);
            Assert.DoesNotContain("Synthetic Office", UsageAnalysis.CapabilityCatalog(catalog, false));
            Assert.DoesNotContain("Synthetic Home", UsageAnalysis.CapabilityCatalog(catalog, true));
            Assert.Null(WifiProfileCapabilities.ConnectedDescriptor.Fields.Single().DefaultValue);
            Assert.True(WifiProfileCapabilities.ConnectedDescriptor.Fields.Single().EditAsList);
            var localDefaults = catalog with { Capabilities = [.. catalog.Capabilities.Select(c => c.Id == WifiProfileCapabilities.Connected ? c with
                { Fields = [.. c.Fields.Select(f => f with { DefaultValue = "synthetic-private-profile", Choices = [new("synthetic-private-profile", "synthetic-private-profile")] })] } : c)] };
            Assert.DoesNotContain("synthetic-private-profile", UsageAnalysis.CapabilityCatalog(localDefaults, false));
            var recorder = new UsageRecorder(new(5, true, false, []), DateTimeOffset.UnixEpoch);
            var proposal = new UsageImport(1, recorder.Session.Id, "existing", "synthetic suggestion", "synthetic evidence", Detection(profiles: ""),
                Reminder() with { Parameters = Reminder().Parameters.SetItem("title", "").SetItem("body", "") });
            var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
            Assert.True(draft.Paused); Assert.Contains("Wi-Fi 配置文件名", UsageAnalysis.MissingManualFields(draft, catalog));
            Assert.Throws<ArgumentException>(() => compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection() }), recorder.Session, catalog));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }
}
