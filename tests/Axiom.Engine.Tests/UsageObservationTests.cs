using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class UsageObservationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static string App(string name) => Path.Combine(Path.GetTempPath(), name + ".exe");
    private static UsageRecorder Recorder() => new(new(5, true, true, ["Private.exe"]), Now);
    private static AutomationSnapshot Catalog()
    {
        var modules = new IAutomationPlugin[] { new ProgramPlugin(), new WindowPlugin() };
        return new([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", Now, []);
    }
    [Fact]
    public void ExclusionsPauseDeduplicationAndDeadlineApplyBeforePersistence()
    {
        var recorder = Recorder();
        Assert.False(recorder.Add(new(Now, "foreground", App("Private"))));
        Assert.True(recorder.Add(new(Now, "foreground", App("Editor"))));
        Assert.False(recorder.Add(new(Now.AddSeconds(1), "foreground", App("Editor"))));
        recorder.State("paused"); Assert.False(recorder.Add(new(Now.AddSeconds(2), "process-start", App("Other"))));
        recorder.State("recording"); Assert.False(recorder.Add(new(Now.AddMinutes(5), "foreground", App("Other"))));
        Assert.Equal("stopped", recorder.Session.State); Assert.Single(recorder.Session.Events); Assert.Single(recorder.Session.Objects);
    }
    [Fact]
    public void CapacityStopsWithoutDroppingAnUnreportedTail()
    {
        var recorder = Recorder();
        for (int i = 0; i <= UsageValidation.MaximumEvents; i++) recorder.Add(new(Now.AddMilliseconds(i), "process-start", App("Editor")));
        Assert.Equal(UsageValidation.MaximumEvents, recorder.Session.Events.Length);
        Assert.Equal("stopped", recorder.Session.State); Assert.Contains(recorder.Session.Gaps, s => s.Contains("上限"));
    }
    [Fact]
    public void OnlyRepeatedEvidenceProducesSuggestionsAndAxiomBreaksSequences()
    {
        var recorder = Recorder();
        for (int i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 60), "process-start", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 4), "process-start", App("Companion")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session));
        Assert.Equal("existing", suggestion.Kind); Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog());
        Assert.True(draft.Paused); Assert.Equal(0, draft.Version); Assert.Equal(App("Companion"), draft.Action.Parameters["executable"]);
        var engine = new AutomationTaskCompiler(); var module = new ProgramPlugin();
        foreach (var d in module.Detections) engine.Register(d); foreach (var a in module.Actions) engine.Register(a);
        Assert.NotNull(engine.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var controlled = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => new[] { e, new UsageEvent(e.AtUtc, "axiom-action", null, "axiom") })] };
        Assert.Empty(UsageAnalysis.Analyze(controlled));
    }
    [Fact]
    public void SummaryUsesRandomLocalAliasesAndNeverExportsPathsOrExcludedNames()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("Editor")));
        var text = UsageAnalysis.Summary(recorder.Session, Catalog());
        Assert.DoesNotContain(App("Editor"), text); Assert.DoesNotContain("Editor.exe", text); Assert.DoesNotContain("Private.exe", text);
        Assert.Contains(recorder.Session.Objects[0].Id, text);
        var second = Recorder(); second.Add(new(Now, "foreground", App("Editor")));
        Assert.NotEqual(recorder.Session.Objects[0].Id, second.Session.Objects[0].Id);
    }
    [Fact]
    public void ImportRejectsUntrustedStructureVersionsObjectsAndFreeExecutionText()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("Editor")));
        string Encode(UsageImport p) => JsonSerializer.Serialize(p);
        var proposal = new UsageImport(1, recorder.Session.Id, "insufficient", "更多信息", "请描述目的");
        Assert.Null(UsageAnalysis.Import(Encode(proposal), recorder.Session, Catalog()).Draft);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Encode(proposal with { Version = 2 }), recorder.Session, Catalog()));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Encode(proposal with { SessionId = Guid.NewGuid().ToString() }), recorder.Session, Catalog()));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import("{\"Version\":1,\"Version\":1}", recorder.Session, Catalog()));
        Assert.Throws<JsonException>(() => UsageAnalysis.Import(Encode(proposal).TrimEnd('}') + ",\"Code\":\"run\"}", recorder.Session, Catalog()));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(new string('x', 65537), recorder.Session, Catalog()));
        var alias = recorder.Session.Objects[0].Id;
        var action = ProgramCapabilities.Describe(true);
        var detection = new ProcessRunningTaskDetection().Descriptor;
        var candidate = proposal with { Kind = "existing", Detection = new(detection.Id, detection.SchemaVersion, ImmutableDictionary<string,string>.Empty.Add("processNames", alias)),
            Action = new(action.Id, 1, action.Fields.Where(f => f.DefaultValue is not null).ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", alias).SetItem("arguments", "--execute untrusted")) };
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Encode(candidate), recorder.Session, Catalog()));
        var valid = candidate with { Action = candidate.Action! with { Parameters = candidate.Action.Parameters.SetItem("arguments", "") } };
        Assert.True(UsageAnalysis.Import(Encode(valid), recorder.Session, Catalog()).Draft!.Paused);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Encode(valid with { Action = valid.Action! with { Parameters = valid.Action.Parameters.SetItem("executable", "app-unknown") } }), recorder.Session, Catalog()));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(Encode(valid with { Action = valid.Action! with { Parameters = valid.Action.Parameters.SetItem("executable", App("Unobserved")) } }), recorder.Session, Catalog()));
    }
    [Fact]
    public async Task ObservationProtocolIsBoundedAndCannotBeUsedByShortcutAgent()
    {
        var request = new AutomationMessage(1, "usage", "test", Now.AddSeconds(10), Usage: new("start", new(5, true, false, [])));
        AutomationProtocol.ValidateRequest(request, Now);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request, Now, shortcutAgent: true));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { StartupEnabled = true }, Now));
        Assert.Throws<ArgumentException>(() => AutomationProtocol.ValidateRequest(request with { Usage = new("start", new(121, true, false, [])) }, Now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(new(1, "invoke", "x", Now.AddSeconds(10), UsageOptions: new(5, true, true, [])), Now));
        using var stream = new MemoryStream(); await AutomationProtocol.WriteAsync(stream, request, default); stream.Position = 0;
        Assert.Equal("start", (await AutomationProtocol.ReadAsync(stream, default)).Usage!.Operation);
    }
    [Fact]
    public void DraftEditorKeepsUnsavedProtectionAndJournalBoundsSessions()
    {
        var editor = new Axiom.WinUI.TaskEditorState();
        var draft = new AutomationTask(Guid.NewGuid().ToString(), 0, "候选", true, new("process.running", 2, ImmutableDictionary<string,string>.Empty), new("program.start", 1, ImmutableDictionary<string,string>.Empty));
        editor.LoadDraft(draft); Assert.True(editor.Dirty); Assert.True(editor.Build().Paused);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path);
            for (var i = 0; i < 15; i++) journal.Save(new UsageRecorder(new(5, true, false, []), Now.AddSeconds(i)).Session);
            Assert.Equal(12, journal.Read(Now).Length); journal.Delete(null); Assert.Empty(journal.Read(Now));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void StorageStopsOnRestartRetainsFeedbackAndDeletesEverythingRequested()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("Editor")));
            using (var journal = new UsageJournal(path)) { journal.Save(recorder.Session); journal.Feedback(recorder.Session.Id, "sample", "ignored"); }
            using (var journal = new UsageJournal(path))
            {
                var session = Assert.Single(journal.Read(Now)); Assert.Equal("stopped", session.State); Assert.NotEmpty(session.Gaps);
                Assert.Contains("sample", journal.Dismissed(session.Id)); journal.Delete(session.Id); Assert.Empty(journal.Read(Now)); Assert.Empty(journal.Dismissed(session.Id));
                journal.Save(session with { StartedUtc = Now.AddDays(-8) }); Assert.Empty(journal.Read(Now));
            }
        }
        finally { File.Delete(path); }
    }
}
