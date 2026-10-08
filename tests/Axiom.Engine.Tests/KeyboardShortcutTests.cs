using Axiom.Contracts;
using Axiom.Infrastructure;
using Axiom.ServiceHost;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class KeyboardShortcutTests
{
    private static ShortcutBinding Binding(string key = "A", string function = KeyboardShortcuts.ShowWindow) => new(Guid.NewGuid().ToString("D"), 0, function,
        new(ShortcutModifiers.Control | ShortcutModifiers.Alt, key), true);
    private sealed class Native : IShortcutNative
    {
        internal readonly Dictionary<int, ShortcutChord> Active = [];
        internal readonly HashSet<ShortcutChord> Occupied = [];
        public bool Register(int id, ShortcutChord chord) { if (Occupied.Contains(chord) || Active.ContainsValue(chord)) return false; Active.Add(id, chord); return true; }
        public void Unregister(int id) => Active.Remove(id);
    }
    [Fact]
    public void ConflictLeavesOriginalRegistrationAndFunctionWorking()
    {
        var count = 0; var actions = new ShortcutActionRegistry(); actions.Register(new(KeyboardShortcuts.ShowWindow, "show", ""), () => count++);
        var native = new Native(); var set = new ShortcutRegistrationSet(actions, native); var original = Binding(); set.Replace([original]);
        var blocked = Binding("B"); native.Occupied.Add(blocked.Chord);
        // A staged successful registration must also be released if a later item conflicts.
        Assert.Throws<InvalidOperationException>(() => set.Replace([original with { Chord = Binding("C").Chord }, blocked]));
        Assert.Equal(original.Chord, Assert.Single(native.Active).Value);
        set.Press(native.Active.Keys.Single()); Assert.Equal(1, count);
        Assert.Equal(original, Assert.Single(set.Read().Shortcuts).Binding);
    }
    [Fact]
    public void SecondRegisteredFunctionDispatchesThroughSameMappingAndDisabledKeysDoNotDispatch()
    {
        var show = 0; var second = 0; var actions = new ShortcutActionRegistry();
        actions.Register(new(KeyboardShortcuts.ShowWindow, "show", ""), () => show++);
        actions.Register(new("test.second", "second", ""), () => second++);
        var native = new Native(); var set = new ShortcutRegistrationSet(actions, native);
        var a = Binding(); var b = Binding("B", "test.second"); set.Replace([a, b]);
        var showId = native.Active.Single(x => x.Value == a.Chord).Key; var secondId = native.Active.Single(x => x.Value == b.Chord).Key;
        set.Press(showId); set.Press(secondId); Assert.Equal(1, show); Assert.Equal(1, second);
        set.Replace([a with { Enabled = false }, b]); set.Press(showId); Assert.Equal(1, show); Assert.Single(native.Active);
        set.Replace([]); set.Press(secondId); Assert.Equal(1, second); Assert.Empty(native.Active);
        Assert.Throws<ArgumentException>(() => set.Replace([Binding("C", "shell.execute")]));
        Assert.Throws<ArgumentException>(() => actions.Register(new("test.second", "", ""), () => { }));
    }
    [Fact]
    public void RestoreReportsOccupiedAndUnavailableFunctionsWhileOtherKeysRemainRegistered()
    {
        var actions = new ShortcutActionRegistry(); actions.Register(new(KeyboardShortcuts.ShowWindow, "show", ""), () => { });
        var native = new Native(); var set = new ShortcutRegistrationSet(actions, native); var occupied = Binding(); native.Occupied.Add(occupied.Chord);
        var snapshot = set.Replace([occupied, Binding("B"), Binding("C", "removed.function")], restore: true);
        Assert.True(snapshot.ListenerAvailable); Assert.Single(snapshot.Shortcuts.Where(x => x.Registered));
        Assert.Equal(2, snapshot.Shortcuts.Count(x => x.Error is not null));
    }
    [Fact]
    public void StorePersistsVersionsAndDeletionDoesNotRecreateDefaultOnRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteShortcutStore(path))
            {
                var original = Assert.Single(store.Read()); Assert.Equal("Ctrl + Alt + A", original.Chord.ToString());
                var changed = store.Save(original with { Chord = Binding("B").Chord }); Assert.Equal(2, changed.Version);
                Assert.Throws<InvalidOperationException>(() => store.Save(original));
                Assert.Throws<ArgumentException>(() => store.Save(Binding("B")));
                Assert.Equal(changed, Assert.Single(store.Read()));
                Assert.Throws<InvalidOperationException>(() => store.Delete(changed.Id, 1));
                store.Delete(changed.Id, changed.Version);
            }
            using var reopened = new SqliteShortcutStore(path); Assert.Empty(reopened.Read());
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void ProtocolRejectsUnsupportedCombinationsAndAgentOperationsAtClientBoundary()
    {
        var now = DateTimeOffset.UtcNow; var request = new AutomationMessage(1, "save-shortcut", "test", now.AddSeconds(10), Shortcut: Binding());
        AutomationProtocol.ValidateRequest(request, now);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { StartupEnabled = true }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Type = "read-shortcuts" }, now));
        var agent = new AutomationMessage(1, "apply-shortcuts", "agent", now.AddSeconds(10), Bindings: [Binding()]);
        AutomationProtocol.ValidateRequest(agent, now, shortcutAgent: true);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(agent, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(agent with { Id = "anything" }, now, shortcutAgent: true));
        Assert.Throws<ArgumentException>(() => KeyboardShortcuts.Validate(Binding() with { Chord = new(ShortcutModifiers.Shift, "A") }));
        Assert.Throws<ArgumentException>(() => KeyboardShortcuts.Validate(Binding() with { Chord = new(ShortcutModifiers.Control, "F12") }));
        var item = Binding(); Assert.Throws<ArgumentException>(() => KeyboardShortcuts.ValidateSet([item, item with { Id = Guid.NewGuid().ToString("D") }]));
        Assert.Throws<ArgumentException>(() => KeyboardShortcuts.ValidateSet(Enumerable.Range(0, 33).Select(_ => Binding() with { Enabled = false })));
    }
    private sealed class Store(ShortcutBinding initial) : IShortcutStore
    {
        internal ShortcutBinding[] Items = [initial]; internal bool Fail;
        public ShortcutBinding[] Read() => Items;
        public ShortcutBinding Save(ShortcutBinding binding) { if (Fail) throw new IOException("disk full"); var saved = binding with { Version = binding.Version + 1 }; Items = [saved]; return saved; }
        public void Delete(string id, long version) { if (Fail) throw new IOException("disk full"); Items = []; }
    }
    private sealed class Agent : IShortcutAgentClient
    {
        internal ShortcutBinding[] Active = []; internal int Calls; internal bool LoseApplyReply;
        public Task<ShortcutSnapshot> RequestAsync(string operation, ShortcutBinding[]? bindings, CancellationToken token)
        {
            Calls++; if (bindings is not null) Active = bindings;
            if (operation == "apply-shortcuts" && LoseApplyReply) throw new IOException("reply lost");
            return Task.FromResult(new ShortcutSnapshot([new(KeyboardShortcuts.ShowWindow, "show", "")], [.. Active.Select(x => new ShortcutState(x, x.Enabled))], true));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPersistenceOrLostApplyReplyRestoresPreviousRegistration(bool loseReply)
    {
        var binding = Binding() with { Version = 1 }; var store = new Store(binding) { Fail = !loseReply };
        var agent = new Agent { Active = [binding], LoseApplyReply = loseReply }; var coordinator = new ShortcutCoordinator(store, agent);
        await Assert.ThrowsAsync<IOException>(() => coordinator.HandleAsync(new(1, "save-shortcut", "test", Shortcut: binding with { Chord = Binding("B").Chord }), CancellationToken.None));
        Assert.Equal(binding, Assert.Single(store.Items)); Assert.Equal(binding, Assert.Single(agent.Active));
    }
    [Fact]
    public async Task StaleVersionIsRejectedBeforeRegisteringAndSuccessfulSaveUsesNewVersion()
    {
        var binding = Binding() with { Version = 2 }; var store = new Store(binding); var agent = new Agent { Active = [binding] };
        var coordinator = new ShortcutCoordinator(store, agent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(new(1, "save-shortcut", "test", Shortcut: binding with { Version = 1 }), CancellationToken.None));
        Assert.Equal(0, agent.Calls);
        var snapshot = await coordinator.HandleAsync(new(1, "save-shortcut", "test", Shortcut: binding with { Chord = Binding("B").Chord }), CancellationToken.None);
        Assert.Equal(3, Assert.Single(store.Items).Version); Assert.True(Assert.Single(snapshot.Shortcuts).Registered);
        await coordinator.HandleAsync(new(1, "delete-shortcut", "test", Id: binding.Id, ExpectedVersion: 3), CancellationToken.None);
        Assert.Empty(store.Items); Assert.Empty(agent.Active);
    }
}
