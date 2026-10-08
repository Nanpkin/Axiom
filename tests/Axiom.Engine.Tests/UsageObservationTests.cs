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
        var modules = new IAutomationPlugin[] { new ProgramPlugin(), new WindowPlugin(), new SystemEventPlugin(), new WindowPresencePlugin() };
        return new([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", Now, []);
    }
    [Theory]
    [InlineData("maximized", "maximize")]
    [InlineData("left", "left")]
    [InlineData("right", "right")]
    [InlineData("center", "center")]
    public void RepeatedWindowPreparationOffersSingleReviewedStateCommandAndHonorsMissingEvidence(string observedState, string command)
    {
        var recorder = new UsageRecorder(new(5, false, false, [], WindowStates: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 60), "window-shown", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 5), "window-" + observedState, App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 10), "window-hidden", App("Editor")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session)); Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal(WindowPresenceCapabilities.Visible, draft.Detection.CapabilityId); Assert.Equal(WindowCapabilities.State, draft.Action.CapabilityId);
        Assert.Equal(command, draft.Action.Parameters["state"]); Assert.Equal(App("Editor"), draft.Action.Parameters["executable"]);
        Assert.NotEqual(UsageAnalysis.FeedbackKey(recorder.Session, suggestion), UsageAnalysis.FeedbackKey(recorder.Session, suggestion with { WindowState = "minimize" }));
        var missing = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => new[] { e, new UsageEvent(e.AtUtc, "gap", null, "unknown") })] };
        Assert.Empty(UsageAnalysis.Analyze(missing));
        var automated = recorder.Session with { Events = [new UsageEvent(Now, "axiom-action", recorder.Session.Objects[0].Id, "axiom"), .. recorder.Session.Events] };
        Assert.Empty(UsageAnalysis.Analyze(automated));
    }

    [Fact]
    public void RepeatedExitPairsOfferOnlyReviewedGracefulCleanupAndIgnoreAutomationOrMissingEvidence()
    {
        var recorder = new UsageRecorder(new(5, false, false, [], ProcessExits: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 60), "process-exit", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 5), "process-exit", App("Companion")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session)); Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal("process.exited", draft.Detection.CapabilityId); Assert.Equal("program.close", draft.Action.CapabilityId);
        Assert.Equal("graceful", draft.Action.Parameters["mode"]); Assert.Equal("latest", draft.Action.Parameters["instances"]);
        var compiler = new AutomationTaskCompiler(); var module = new ProgramPlugin();
        foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a);
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var automated = recorder.Session with { Events = [new UsageEvent(Now, "axiom-action", recorder.Session.Objects[1].Id, "axiom"), .. recorder.Session.Events] };
        Assert.Empty(UsageAnalysis.Analyze(automated));
        var missing = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => new[] { e, new UsageEvent(e.AtUtc, "gap", null, "unknown") })] };
        Assert.Empty(UsageAnalysis.Analyze(missing));
    }

    [Fact]
    public void ExitObservationIsExplicitAndDoesNotInferManualClosure()
    {
        var at = DateTimeOffset.UtcNow; var options = new UsageOptions(5, false, false, [], ProcessExits: true);
        UsageValidation.Options(options); var recorder = new UsageRecorder(options, at);
        Assert.False(recorder.Add(new(at, "process-start", App("Editor"))));
        Assert.True(recorder.Add(new(at.AddSeconds(5), "process-exit", App("Editor"))));
        var exit = Assert.Single(recorder.Session.Events); Assert.Equal("unknown", exit.Origin); Assert.Null(exit.DwellSeconds);
        Assert.Empty(UsageAnalysis.Analyze(recorder.Session));
        Assert.False(Recorder().Add(new(Now, "process-exit", App("Editor"))));
        Assert.Contains("ProcessExits", UsageAnalysis.Summary(recorder.Session, Catalog()));
    }

    [Fact]
    public void UnobservedForegroundClosesDwellWithoutRetainingAnObjectOrRepeatingGaps()
    {
        var at = DateTimeOffset.UtcNow; var recorder = new UsageRecorder(new(5, true, false, []), at);
        recorder.Add(new(at, "foreground", Path.Combine(Path.GetTempPath(), "Example.exe")));
        recorder.Add(new(at.AddSeconds(5), "foreground-unobserved"));
        Assert.Equal(5, recorder.Session.Events[0].DwellSeconds);
        Assert.Single(recorder.Session.Objects); Assert.Equal("gap", recorder.Session.Events[1].Kind);
        Assert.False(recorder.Add(new(at.AddSeconds(10), "foreground-unobserved"))); Assert.Equal(2, recorder.Session.Events.Length);
        recorder.Add(new(at.AddSeconds(20), "foreground", Path.Combine(Path.GetTempPath(), "Example.exe")));
        Assert.Equal(3, recorder.Session.Events.Length); Assert.Null(recorder.Session.Events[2].DwellSeconds);
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
    public void RepeatedPostUnlockStartsHaveRepresentativeEvidenceAndOnlyOneCountPerOpportunity()
    {
        var recorder = new UsageRecorder(new(5, false, true, [], SystemEvents: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 70), "system-unlocked"));
            recorder.Add(new(Now.AddSeconds(i * 70 + 5), "process-start", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 70 + 6), "process-start", App("Editor")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session));
        Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities); Assert.Equal("unlocked", suggestion.EventKind);
        Assert.Equal(new UsageEvidence(0, 5), suggestion.EvidenceSamples![0]);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal(SystemEventCapabilities.Event, draft.Detection.CapabilityId); Assert.Equal("unlocked", draft.Detection.Parameters["event"]);
        var missing = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => e.Kind == "system-unlocked" ? new[] { e, new UsageEvent(e.AtUtc, "gap", null, "unknown") } : new[] { e })] };
        Assert.Empty(UsageAnalysis.Analyze(missing));
    }
    [Fact]
    public void ForegroundDwellMergesAcrossOtherEventsAndExclusionsBreakPatterns()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("Editor")));
        recorder.Add(new(Now.AddSeconds(1), "process-start", App("Other")));
        Assert.False(recorder.Add(new(Now.AddSeconds(2), "foreground", App("Editor"))));
        recorder.Add(new(Now.AddSeconds(5), "foreground", App("Private")));
        Assert.Equal(5d, recorder.Session.Events[0].DwellSeconds);
        Assert.Contains(recorder.Session.Events, e => e.Kind == "gap"); Assert.DoesNotContain(recorder.Session.Objects, o => o.Executable == App("Private"));
        recorder.Add(new(Now.AddSeconds(10), "foreground", App("Editor")));
        recorder.State("stopped", atUtc: Now.AddSeconds(20));
        Assert.Equal(10d, recorder.Session.Events.Last().DwellSeconds);
        Assert.Equal(Now.AddSeconds(20), recorder.Session.StoppedUtc);
        var summary = UsageAnalysis.Summary(recorder.Session, Catalog()); Assert.Contains("stoppedAfterSeconds", summary); Assert.Contains("plannedMinutes", summary);
    }
    [Fact]
    public void CapabilityExportsShareLiveRegistrationAndFeedbackStaysLocalAcrossSessions()
    {
        var catalog = Catalog(); var json = UsageAnalysis.CapabilityCatalog(catalog, false); var readable = UsageAnalysis.CapabilityCatalog(catalog, true);
        foreach (var capability in catalog.Capabilities) { Assert.Contains(capability.Id, json); Assert.Contains(capability.Id, readable); }
        Assert.Contains("network-up =", readable);
        var one = Recorder(); one.Add(new(Now, "foreground", App("Editor"))); var two = Recorder(); two.Add(new(Now, "foreground", App("Editor")));
        var first = new UsageSuggestion("switch:any", "insufficient", "", "", "", [one.Session.Objects[0].Id], 3, 3);
        var second = first with { ObjectIds = [two.Session.Objects[0].Id] };
        Assert.Equal(UsageAnalysis.FeedbackKey(one.Session, first), UsageAnalysis.FeedbackKey(two.Session, second));
        Assert.DoesNotContain(UsageAnalysis.FeedbackKey(one.Session, first), UsageAnalysis.Summary(one.Session, catalog));
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
        var marked = recorder.Session with { Events = [new UsageEvent(Now, "axiom-action", recorder.Session.Objects[0].Id, "axiom"), .. recorder.Session.Events] };
        Assert.Empty(UsageAnalysis.Analyze(marked));
    }
    [Fact]
    public void FullRegisteredCatalogFitsExportAndOversizedDescriptionsAreSummarized()
    {
        var modules = new IAutomationPlugin[] { new ProgramPlugin(), new WindowPlugin(), new SystemEventPlugin(), new AxiomStartupPlugin(), new KeyboardPlugin(), new TrayPlugin(), new OpenTargetPlugin(), new ForegroundPlugin(), new WindowPresencePlugin() };
        var catalog = Catalog() with { Capabilities = [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor))).DistinctBy(c => (c.Kind, c.Id))], Combinations = [.. modules.SelectMany(m => m.Combinations)] };
        var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("Editor")));
        var normal = UsageAnalysis.Prompt + UsageAnalysis.Summary(recorder.Session, catalog);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(normal) <= UsageValidation.MaximumExportBytes);
        var large = catalog with { Capabilities = [.. catalog.Capabilities.Select(c => c with { Description = new string('说', 10000) })] };
        var compact = UsageAnalysis.Summary(recorder.Session, large);
        Assert.Contains("省略能力说明", compact);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(UsageAnalysis.Prompt + compact) <= UsageValidation.MaximumExportBytes);
        foreach (var capability in catalog.Capabilities) Assert.Contains(capability.Id, compact);
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
