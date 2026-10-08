using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class HotkeyCapabilityTests
{
    private static readonly ValueSchema EventSchema = new("event-count", 1);
    private static readonly ResourceKey EventKey = new("input", "keypress", ResourceScope.Machine);
    private static SourceSession Events(AutomationFixture f)
    {
        f.Engine.RegisterCapability(new(AutomationFixture.Binding("input", schema: EventSchema).Descriptor, ScalarKind.Number, ScalarKind.Number));
        return f.Engine.RegisterSource("hotkeys", [EventKey]);
    }
    private static Observation Sample(AutomationFixture f, decimal count) => new(EventKey, StateQuality.Known, StateValue.From(EventSchema, count), f.Clock.UtcNow);
    private static RuleDefinition Rule(string id = "hotkey", RuleTiming? timing = null) => new(id, 1, "owner", ResourceScope.Machine,
        RuleCondition.Event(EventKey, EventSchema), RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)), timing: timing);

    [Fact(Timeout = 10000)]
    public async Task RepeatedPressesRunOnceSnapshotsNeverReplayAndBusyEventsAreDiscarded()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor); var source = Events(f);
        f.Engine.ApplySnapshot(source, 0, [Sample(f, 10)]); f.Engine.SetRule(Rule()); f.Engine.StartExecution();
        await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Engine.Update(source, 1, Sample(f, 11)); var first = await executor.NextAsync();
        f.Engine.Update(source, 2, Sample(f, 12)); Assert.False(first.Token.IsCancellationRequested);
        first.Complete(); await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
        f.Engine.Update(source, 3, Sample(f, 13)); (await executor.NextAsync()).Complete(); await f.Engine.WaitForIdleAsync();
        Assert.Equal(2, executor.Count);
        f.Engine.ApplySnapshot(source, 4, [Sample(f, 99)]); await f.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
        f.Engine.Update(source, 5, Sample(f, 99)); await f.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
        f.Engine.Update(source, 6, Sample(f, 100)); (await executor.NextAsync()).Complete(); await f.Engine.WaitForIdleAsync(); Assert.Equal(3, executor.Count);
    }
    [Fact(Timeout = 10000)]
    public async Task DelayedEventFiresWithoutFurtherInputAndPauseCancelsAnotherPendingEvent()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor); var source = Events(f);
        f.Engine.ApplySnapshot(source, 0, [Sample(f, 0)]); f.Engine.SetRule(Rule(timing: new(minimumDuration: TimeSpan.FromSeconds(2)))); f.Engine.StartExecution();
        f.Engine.Update(source, 1, Sample(f, 1)); f.Scheduler.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(0, executor.Count);
        f.Scheduler.Advance(TimeSpan.FromSeconds(1)); (await executor.NextAsync()).Complete(); await f.Engine.WaitForIdleAsync();
        f.Engine.Update(source, 2, Sample(f, 2)); f.Engine.RemoveRule("hotkey"); f.Scheduler.Advance(TimeSpan.FromSeconds(3));
        await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count); Assert.Equal(0, f.Engine.PendingDeadlines);
    }
    [Fact(Timeout = 10000)]
    public async Task ConsumedEventDoesNotKeepClaimingActionAgainstAnotherRule()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor); var source = Events(f);
        f.Engine.ApplySnapshot(source, 0, [Sample(f, 0)]); f.Engine.SetRule(Rule());
        var secondKey = new ResourceKey(EventKey.CapabilityId, "other-key", EventKey.Scope);
        var other = f.Engine.RegisterSource("other-hotkey", [secondKey]);
        Observation Other(decimal count) => new(secondKey, StateQuality.Known, StateValue.From(EventSchema, count), f.Clock.UtcNow);
        f.Engine.ApplySnapshot(other, 0, [Other(0)]);
        f.Engine.SetRule(new("other", 1, "owner", ResourceScope.Machine, RuleCondition.Event(secondKey, EventSchema),
            RuleAction.Command(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, false))));
        f.Engine.StartExecution(); f.Engine.Update(source, 1, Sample(f, 1)); (await executor.NextAsync()).Complete(); await f.Engine.WaitForIdleAsync();
        f.Engine.Update(other, 1, Other(1)); var call = await executor.NextAsync(); Assert.False(call.Command!.Arguments.Boolean);
        call.Complete(); await f.Engine.WaitForIdleAsync(); Assert.Equal(2, executor.Count);
    }
    private sealed class Native : IShortcutNative
    {
        internal readonly Dictionary<int, ShortcutChord> Active = [];
        public bool Register(int id, ShortcutChord chord) { if (Active.ContainsValue(chord)) return false; Active.Add(id, chord); return true; }
        public void Unregister(int id) => Active.Remove(id);
    }
    [Fact]
    public void AppShortcutAndRulesShareOneRegistrationAndReleaseOnlyLastSubscriber()
    {
        var native = new Native(); var hub = new HotkeyRegistrationHub(native); var chord = HotkeyCapabilities.Parse("Ctrl + Alt + K");
        var app = 0; var rules = 0;
        Assert.True(hub.Register("app", chord, () => app++)); Assert.True(hub.Register("rule1", chord, () => rules++)); Assert.True(hub.Register("rule2", chord, () => rules++));
        var id = Assert.Single(native.Active).Key; hub.Press(id); Assert.Equal(1, app); Assert.Equal(2, rules);
        hub.Release("app"); hub.Release("rule1"); Assert.Single(native.Active); hub.Press(id); Assert.Equal(1, app); Assert.Equal(3, rules);
        hub.Release("rule2"); Assert.Empty(native.Active); hub.Press(id); Assert.Equal(3, rules);
    }
    [Fact]
    public void DetectionDeduplicatesButActionPreservesSequenceAndRejectsInvalidKeys()
    {
        var text = "ctrl+alt+k\nCtrl + Alt + K\nCtrl+Shift+S";
        Assert.Equal(2, HotkeyCapabilities.ParseList(text, true).Length); Assert.Equal(3, HotkeyCapabilities.ParseList(text, false).Length);
        Assert.Equal("Enter", HotkeyCapabilities.ParseList("Enter", false).Single().Key);
        Assert.Throws<ArgumentException>(() => HotkeyCapabilities.ParseList("Ctrl+F12", true));
        Assert.Throws<ArgumentException>(() => HotkeyCapabilities.ParseList("K", true));
        Assert.Throws<ArgumentException>(() => HotkeyCapabilities.ParseList("Ctrl+Ctrl+K", false));
        Assert.Throws<ArgumentException>(() => HotkeyCapabilities.ParseList(string.Join("\n", Enumerable.Repeat("Ctrl+K", 17)), false));
    }
    [Fact]
    public async Task KeyboardModulesCompileAndPersistThroughExistingTaskStore()
    {
        var compiler = new AutomationTaskCompiler(); var plugin = new KeyboardPlugin();
        foreach (var detection in plugin.Detections) compiler.Register(detection); foreach (var action in plugin.Actions) compiler.Register(action);
        var config = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "hotkey", false,
            new(HotkeyCapabilities.Pressed, 1, ImmutableDictionary<string,string>.Empty.Add("chords", "Ctrl+Alt+K\nCtrl+Alt+L")),
            new(HotkeyCapabilities.Send, 1, ImmutableDictionary<string,string>.Empty.Add("chords", "Ctrl+C\nCtrl+C").Add("delaySeconds", "1")));
        var rule = compiler.Compile(config, "owner", ResourceScope.Session("owner", 1)); Assert.Equal(ConditionKind.Event, rule.Condition.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), rule.Timing.MinimumDuration);
        var reordered = compiler.Compile(config with { Action = config.Action with { Parameters = config.Action.Parameters.SetItem("chords", "Ctrl+C") } }, "owner", rule.Scope);
        Assert.NotEqual(rule.Action.Key, reordered.Action.Key); // Distinct recipes do not keep claiming each other's resource.
        var path = Path.Combine(Path.GetTempPath(), "axiom-keys-" + Guid.NewGuid().ToString("N"), "tasks.db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) { var saved = await store.SaveAsync(config, default); Assert.Equal(1, saved.Version); }
            using var reopened = new SqliteAutomationTaskStore(path); var restored = (await reopened.ListAsync()).Single();
            Assert.Equal(config.Detection.Parameters["chords"], restored.Detection.Parameters["chords"]); Assert.Equal(config.Action.Parameters["chords"], restored.Action.Parameters["chords"]);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
