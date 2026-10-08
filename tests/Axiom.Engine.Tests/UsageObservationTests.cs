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
        var modules = BuiltInAutomationPlugins.Create();
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

    [Theory]
    [InlineData("left", "left")]
    [InlineData("right", "right")]
    [InlineData("center", "center")]
    [InlineData("maximized", "maximize")]
    public void ForegroundPreparationUsesIndependentOpportunitiesAndReviewedActualCombination(string state, string command)
    {
        var recorder = new UsageRecorder(new(5, true, false, [], WindowStates: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 60), "foreground", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 5), "window-" + state, App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 6), "window-" + state, App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 7), "foreground", App("Other")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session), s => s.Id.StartsWith("focus-layout:"));
        Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities); Assert.Contains("来源未知", suggestion.Limitation);
        Assert.Equal(3, suggestion.EvidenceSamples!.Length);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog());
        Assert.True(draft.Paused); Assert.Equal(0, draft.Version); Assert.Equal(ForegroundCapabilities.Active, draft.Detection.CapabilityId);
        Assert.Equal("Editor.exe", draft.Detection.Parameters["processNames"]); Assert.Equal(command, draft.Action.Parameters["state"]);
        var compiler = new AutomationTaskCompiler(); compiler.Register(new ForegroundTaskDetection()); compiler.Register(new WindowStateTaskAction());
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        Assert.NotEqual(UsageAnalysis.FeedbackKey(recorder.Session, suggestion), UsageAnalysis.FeedbackKey(recorder.Session, suggestion with { Id = "window:other" }));
        foreach (var boundary in new[] { "gap", "system-locked", "axiom-action", "window-hidden", "process-exit", "foreground" })
        {
            var interrupted = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => e.Kind == "foreground" ? new[] { e, new UsageEvent(e.AtUtc, boundary, boundary == "foreground" ? null : e.ObjectId, boundary == "axiom-action" ? "axiom" : "unknown") } : new[] { e })] };
            Assert.DoesNotContain(UsageAnalysis.Analyze(interrupted), s => s.Id.StartsWith("focus-layout:"));
        }
        var automated = recorder.Session with { Events = [.. recorder.Session.Events.Select(e => e.Kind.StartsWith("window-") ? e with { Origin = "axiom" } : e)] };
        Assert.DoesNotContain(UsageAnalysis.Analyze(automated), s => s.Id.StartsWith("focus-layout:"));
        var late = recorder.Session with { Events = [.. recorder.Session.Events.Select(e => e.Kind.StartsWith("window-") ? e with { AtUtc = e.AtUtc.AddSeconds(30) } : e)] };
        Assert.DoesNotContain(UsageAnalysis.Analyze(late), s => s.Id.StartsWith("focus-layout:"));
        var shortEvidence = recorder.Session with { Events = [.. recorder.Session.Events.Take(8)] };
        Assert.DoesNotContain(UsageAnalysis.Analyze(shortEvidence), s => s.Id.StartsWith("focus-layout:"));
    }

    [Theory]
    [InlineData("system-unlocked", "system.event", "event", "unlocked")]
    [InlineData("system-resumed", "system.event", "event", "resumed")]
    [InlineData("system-usb-arrived", "system.event", "event", "usb-arrived")]
    [InlineData("audio-output", "audio.default", "endpoint", "output")]
    [InlineData("audio-input", "audio.default", "endpoint", "input")]
    public void RepeatedEnvironmentPreparationOffersWindowLayoutWithSeparateEventEvidence(string kind, string capability, string field, string value)
    {
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true, WindowStates: true, AudioEvents: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 70), kind));
            recorder.Add(new(Now.AddSeconds(i * 70 + 5), "window-left", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 70 + 6), "window-right", App("Editor"))); // one observation opportunity, not two repeats
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session));
        Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities); Assert.Contains("来源未知", suggestion.Limitation);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal(capability, draft.Detection.CapabilityId); Assert.Equal(value, draft.Detection.Parameters[field]); Assert.Equal("left", draft.Action.Parameters["state"]);
        var compiler = new AutomationTaskCompiler(); compiler.Register(new SystemEventTaskDetection()); compiler.Register(new AudioTaskDetection()); compiler.Register(new WindowStateTaskAction());
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var mixed = recorder.Session with { Events = [.. recorder.Session.Events.Select((e, index) => index == 0 ? e with { Kind = kind == "system-unlocked" ? "system-resumed" : "system-unlocked" } : e)] };
        Assert.Empty(UsageAnalysis.Analyze(mixed)); // different environmental triggers cannot be pooled to reach three
        foreach (var boundary in new[] { "gap", "system-locked", "axiom-action" })
        {
            var interrupted = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => e.Kind == kind ? new[] { e, new UsageEvent(e.AtUtc, boundary, null, boundary == "axiom-action" ? "axiom" : "unknown") } : new[] { e })] };
            Assert.Empty(UsageAnalysis.Analyze(interrupted));
        }
        var automated = recorder.Session with { Events = [.. recorder.Session.Events.Select(e => e.Kind == "window-left" ? e with { Origin = "axiom" } : e)] };
        Assert.Empty(UsageAnalysis.Analyze(automated));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("input")]
    public void RepeatedAudioPreparationKeepsRoleLimitsAndCreatesOnlyAPausedReviewedRule(string endpoint)
    {
        var recorder = new UsageRecorder(new(5, false, true, [], AudioEvents: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 70), "audio-" + endpoint));
            recorder.Add(new(Now.AddSeconds(i * 70 + 5), "process-start", App("AudioTool")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session)); Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities);
        Assert.Contains("不能筛选具体设备", suggestion.Limitation);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal(AudioCapabilities.Changed, draft.Detection.CapabilityId); Assert.Equal(endpoint, draft.Detection.Parameters["endpoint"]);
        var compiler = new AutomationTaskCompiler(); compiler.Register(new AudioTaskDetection()); compiler.Register(new ProgramTaskAction(true));
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        Assert.Contains("AudioEvents", UsageAnalysis.Summary(recorder.Session, Catalog()));
        var automated = recorder.Session with { Events = [new UsageEvent(Now, "axiom-action", recorder.Session.Objects[0].Id, "axiom"), .. recorder.Session.Events] };
        Assert.Empty(UsageAnalysis.Analyze(automated));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("center")]
    public void RepeatedDisplayLayoutsUseSystemTriggerAndNeverAssumeAParticularMonitor(string layout)
    {
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true, WindowStates: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 70), "system-display-changed"));
            recorder.Add(new(Now.AddSeconds(i * 70 + 5), "window-" + layout, App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 70 + 6), "window-" + layout, App("Editor")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session)); Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities);
        Assert.Contains("不能识别特定屏幕组合", suggestion.Limitation);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal("系统变化后整理窗口", draft.Title);
        Assert.Equal(SystemEventCapabilities.Event, draft.Detection.CapabilityId); Assert.Equal("display-changed", draft.Detection.Parameters["event"]);
        Assert.Equal(WindowCapabilities.State, draft.Action.CapabilityId); Assert.Equal(layout, draft.Action.Parameters["state"]);
        var compiler = new AutomationTaskCompiler(); compiler.Register(new SystemEventTaskDetection()); compiler.Register(new WindowStateTaskAction());
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var interrupted = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => e.Kind == "system-display-changed" ? new[] { e, new UsageEvent(e.AtUtc, "gap", null, "unknown") } : new[] { e })] };
        Assert.Empty(UsageAnalysis.Analyze(interrupted));
        var automated = recorder.Session with { Events = [new UsageEvent(Now, "axiom-action", recorder.Session.Objects[0].Id, "axiom"), .. recorder.Session.Events] };
        Assert.Empty(UsageAnalysis.Analyze(automated));
        var noWindowScope = new UsageRecorder(new(5, false, false, [], SystemEvents: true), Now);
        Assert.False(noWindowScope.Add(new(Now, "window-" + layout, App("Editor"))));
    }

    [Fact]
    public void RepeatedHiddenWindowCleanupKeepsEvidenceLimitsAndCompilesOnlyPausedGracefulDraft()
    {
        var recorder = new UsageRecorder(new(5, false, false, [], ProcessExits: true, WindowStates: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 60), "window-hidden", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 5), "process-exit", App("Companion")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 6), "process-exit", App("Companion")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session));
        Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities);
        Assert.Contains("不能从一个窗口消失", suggestion.Limitation);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal(WindowPresenceCapabilities.Absent, draft.Detection.CapabilityId);
        Assert.Equal("program.close", draft.Action.CapabilityId); Assert.Equal("graceful", draft.Action.Parameters["mode"]); Assert.Equal("latest", draft.Action.Parameters["instances"]);
        var compiler = new AutomationTaskCompiler();
        foreach (var module in new IAutomationPlugin[] { new ProgramPlugin(), new WindowPresencePlugin() })
        { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        Assert.Contains("window-exit:", UsageAnalysis.Summary(recorder.Session, Catalog()));
        foreach (var boundary in new[] { "gap", "window-shown", "axiom-action" })
        {
            var interrupted = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => e.Kind == "window-hidden"
                ? new[] { e, new UsageEvent(e.AtUtc.AddSeconds(1), boundary, e.ObjectId, boundary == "axiom-action" ? "axiom" : "unknown") } : new[] { e })] };
            Assert.Empty(UsageAnalysis.Analyze(interrupted));
        }
        var automated = recorder.Session with { Events = [new UsageEvent(Now, "axiom-action", recorder.Session.Objects[1].Id, "axiom"), .. recorder.Session.Events] };
        Assert.Empty(UsageAnalysis.Analyze(automated));
        var sameApp = recorder.Session with { Events = [.. recorder.Session.Events.Select(e => e with { ObjectId = recorder.Session.Objects[0].Id })] };
        Assert.Empty(UsageAnalysis.Analyze(sameApp));
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
    [Theory]
    [InlineData("unlocked")]
    [InlineData("battery-low20")]
    [InlineData("battery-low10")]
    [InlineData("usb-arrived")]
    public void RepeatedSystemEventStartsHaveRepresentativeEvidenceAndOnlyOneCountPerOpportunity(string kind)
    {
        var recorder = new UsageRecorder(new(5, false, true, [], SystemEvents: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 70), "system-" + kind));
            recorder.Add(new(Now.AddSeconds(i * 70 + 5), "process-start", App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 70 + 6), "process-start", App("Editor")));
        }
        var suggestion = Assert.Single(UsageAnalysis.Analyze(recorder.Session));
        Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities); Assert.Equal(kind, suggestion.EventKind);
        if (kind == "usb-arrived") Assert.Contains("任意USB设备", suggestion.Limitation);
        Assert.Equal(new UsageEvidence(0, 5), suggestion.EvidenceSamples![0]);
        var draft = UsageAnalysis.Draft(recorder.Session, suggestion, Catalog()); Assert.True(draft.Paused);
        Assert.Equal(SystemEventCapabilities.Event, draft.Detection.CapabilityId); Assert.Equal(kind, draft.Detection.Parameters["event"]);
        var missing = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => e.Kind == "system-" + kind ? new[] { e, new UsageEvent(e.AtUtc, "gap", null, "unknown") } : new[] { e })] };
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
    public void FullProductionCatalogAndMaximumObjectDictionaryFitExportWithoutDeviceIdentityOrLostParameters()
    {
        var recorder = new UsageRecorder(new(120, false, true, [], new string('界', 500), SystemEvents: true, WindowStates: true), Now);
        for (var i = 0; i < 128; i++) recorder.Add(new(Now.AddSeconds(i), "process-start", App("Synthetic" + i)));
        for (var repeat = 0; repeat < 3; repeat++)
            for (var i = 0; i < 24; i++) recorder.Add(new(Now.AddSeconds(200 + repeat * 70 + i * 2), "process-start", App("Synthetic" + i)));
        var kinds = new[] { "system-unlocked", "system-resumed", "system-usb-arrived", "system-display-changed" };
        for (var repeat = 0; repeat < 3; repeat++)
            for (var i = 0; i < kinds.Length; i++)
            {
                recorder.Add(new(Now.AddSeconds(500 + repeat * 100 + i * 20), kinds[i]));
                recorder.Add(new(Now.AddSeconds(505 + repeat * 100 + i * 20), "window-left", App("Synthetic" + (100 + i))));
            }
        Assert.Equal(128, recorder.Session.Objects.Length); Assert.Equal(12, UsageAnalysis.Analyze(recorder.Session).Length);
        var hdr = new HdrTaskAction().Descriptor;
        hdr = hdr with { Fields = [.. hdr.Fields.Select(field => field.Kind == TaskFieldKind.Resource
            ? field with { DefaultValue = "synthetic-private-display", Choices = [new("synthetic-private-display", "synthetic-private-label")] } : field)] };
        var catalog = Catalog(); catalog = catalog with { Capabilities = catalog.Capabilities.Add(hdr), Combinations = catalog.Combinations.Add(new("process.running", "display.hdr")) };
        var summary = UsageAnalysis.Summary(recorder.Session, catalog);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(UsageAnalysis.Prompt + summary) <= UsageValidation.MaximumExportBytes);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(UsageAnalysis.Prompt + summary.Replace("\r\n", "\n").Replace("\n", "\r\n")) <= UsageValidation.MaximumExportBytes);
        Assert.DoesNotContain("synthetic-private", summary); Assert.DoesNotContain(App("Synthetic0"), summary);
        using var document = JsonDocument.Parse(summary);
        Assert.Equal(nameof(TaskFieldKind.DirectoryPath), document.RootElement.GetProperty("fieldKinds").GetProperty(((int)TaskFieldKind.DirectoryPath).ToString()).GetString());
        var capabilities = document.RootElement.GetProperty("capabilities").EnumerateArray().ToDictionary(c => c.GetProperty("Id").GetString()!);
        Assert.Equal(catalog.Capabilities.Length, capabilities.Count);
        foreach (var capability in catalog.Capabilities)
        {
            Assert.Equal(capability.SchemaVersion, capabilities[capability.Id].GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(capability.Fields.Length, capabilities[capability.Id].GetProperty("Fields").GetArrayLength());
            var exported = capabilities[capability.Id].Deserialize<TaskCapability>()!;
            foreach (var field in capability.Fields)
            {
                var actual = exported.Fields.Single(f => f.Id == field.Id);
                Assert.Equal(field.Kind, actual.Kind); Assert.Equal(field.Required, actual.Required); Assert.Equal(field.MaximumLength, actual.MaximumLength);
                Assert.Equal(field.Minimum, actual.Minimum); Assert.Equal(field.Maximum, actual.Maximum);
                if (field.Kind is TaskFieldKind.Resource or TaskFieldKind.FilePath or TaskFieldKind.DirectoryPath or TaskFieldKind.ProcessSelection or TaskFieldKind.ExecutableSelection or TaskFieldKind.TraySelection) { Assert.Null(actual.DefaultValue); Assert.Empty(actual.Choices); }
                else { Assert.Equal(field.DefaultValue, actual.DefaultValue); Assert.Equal(field.Choices.Select(c => c.Value), actual.Choices.Select(c => c.Value)); }
            }
        }
        Assert.Equal(catalog.Combinations.Length, document.RootElement.GetProperty("combinations").GetArrayLength());
        Assert.DoesNotContain("synthetic-private", UsageAnalysis.CapabilityCatalog(catalog, false));
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
    [Theory]
    [InlineData("process-start")]
    [InlineData("process-exit")]
    [InlineData("window-shown")]
    [InlineData("system-unlocked")]
    public void KnownAxiomOriginCannotBecomeRepeatedLaborEvenWithoutSeparateActionRecord(string trigger)
    {
        var recorder = new UsageRecorder(new(5, true, true, [], SystemEvents: true, ProcessExits: true, WindowStates: true), Now);
        for (var i = 0; i < 3; i++)
        {
            recorder.Add(new(Now.AddSeconds(i * 60), trigger, trigger.StartsWith("system-") ? null : App("Editor")));
            recorder.Add(new(Now.AddSeconds(i * 60 + 4), trigger == "window-shown" ? "window-left" : trigger == "system-unlocked" ? "process-start" : trigger, trigger == "window-shown" ? App("Editor") : App("Companion")));
        }
        Assert.Single(UsageAnalysis.Analyze(recorder.Session));
        var controlled = recorder.Session with { Events = [.. recorder.Session.Events.Select((e, i) => i % 2 == 1 ? e with { Origin = "axiom" } : e)] };
        Assert.Empty(UsageAnalysis.Analyze(controlled));
        var boundary = recorder.Session with { Events = [.. recorder.Session.Events.SelectMany(e => new[] { e, new UsageEvent(e.AtUtc, "window-normal", null, "axiom") })] };
        Assert.Empty(UsageAnalysis.Analyze(boundary));
    }
    [Fact]
    public void FullRegisteredCatalogFitsExportAndOversizedDescriptionsAreSummarized()
    {
        var modules = new IAutomationPlugin[] { new ProgramPlugin(), new WindowPlugin(), new SystemEventPlugin(), new IntervalPlugin(), new DailyTimePlugin(), new FileArrivalPlugin(), new AudioPlaybackPlugin(), new MicrophonePlugin(), new OnceTimePlugin(), new AudioPlugin(), new AxiomStartupPlugin(), new KeyboardPlugin(), new TrayPlugin(), new OpenTargetPlugin(), new ForegroundPlugin(), new WindowPresencePlugin() };
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
    public void ImportedFolderPlaceholderRequiresManualEditingAndNormalCompileStillRejectsIt()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "process-start", App("WorkTool"))); var catalog = Catalog();
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "文件准备", "合成测试候选",
            new(FileArrivalCapabilities.Arrived, 1, new Dictionary<string, string> { ["directory"] = "", ["kind"] = "pdf" }.ToImmutableDictionary()),
            new("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", recorder.Session.Objects[0].Id)));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(0, draft.Version); Assert.Equal(new[] { "本机文件夹完整路径" }, UsageAnalysis.MissingManualFields(draft, catalog));
        var compiler = new AutomationTaskCompiler(); compiler.Register(new FileArrivalTaskDetection()); compiler.Register(new ProgramTaskAction(true));
        Assert.Throws<ArgumentException>(() => compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var editor = new Axiom.WinUI.TaskEditorState(); editor.LoadDraft(draft); editor.SetParameter(TaskCapabilityKind.Detection, "directory", Path.GetTempPath());
        Assert.True(editor.Dirty); var completed = editor.Build(); Assert.True(completed.Paused); Assert.Empty(UsageAnalysis.MissingManualFields(completed, catalog));
        Assert.NotNull(compiler.Compile(completed, "owner", ResourceScope.Session("owner", 1)));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = proposal.Detection! with { Parameters = proposal.Detection.Parameters.SetItem("kind", "arbitrary") } }), recorder.Session, catalog));
    }
    [Fact]
    public void ImportedUrlPlaceholderDoesNotAcceptUrlsOrRelaxOtherParameters()
    {
        var recorder = Recorder(); var catalog = Catalog();
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "解锁准备", "合成测试候选",
            new(SystemEventCapabilities.Event, 1, ImmutableDictionary<string, string>.Empty.Add("event", "unlocked")),
            new(OpenTargetCapabilities.Open, 1, new Dictionary<string, string> { ["kind"] = "url", ["target"] = "", ["delaySeconds"] = "0" }.ToImmutableDictionary()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.Equal(new[] { "目录/文档路径或网址" }, UsageAnalysis.MissingManualFields(draft, catalog));
        var compiler = new AutomationTaskCompiler(); compiler.Register(new SystemEventTaskDetection()); compiler.Register(new OpenTargetTaskAction());
        Assert.Throws<ArgumentException>(() => compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var completed = draft with { Action = draft.Action with { Parameters = draft.Action.Parameters.SetItem("target", "https://example.invalid/reminder") } };
        Assert.NotNull(compiler.Compile(completed, "owner", ResourceScope.Session("owner", 1)));
        foreach (var (field, value) in new[] { ("target", "https://example.invalid/reminder"), ("delaySeconds", "86401"), ("kind", "script") })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = proposal.Action! with { Parameters = proposal.Action.Parameters.SetItem(field, value) } }), recorder.Session, catalog));
        var audio = new TaskInvocation(AudioPlaybackCapabilities.Set, 1, new Dictionary<string, string> { ["operation"] = "volume", ["percent"] = "0.5", ["delaySeconds"] = "0" }.ToImmutableDictionary());
        var partialDetection = new TaskInvocation(FileArrivalCapabilities.Arrived, 1, new Dictionary<string, string> { ["directory"] = "", ["kind"] = "pdf" }.ToImmutableDictionary());
        var withCombination = catalog with { Combinations = catalog.Combinations.Add(new(FileArrivalCapabilities.Arrived, AudioPlaybackCapabilities.Set)) };
        var invalidAudio = Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = partialDetection, Action = audio }), recorder.Session, withCombination));
        Assert.Contains("整数", invalidAudio.Message);
    }
    [Fact]
    public void ImportedDocumentPlaceholderCannotSupplyLocalPathOrUnsupportedDocumentType()
    {
        var recorder = Recorder(); var catalog = Catalog();
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "计划文档", "合成候选",
            new(SystemEventCapabilities.Event, 1, ImmutableDictionary<string, string>.Empty.Add("event", "unlocked")),
            new(OpenTargetCapabilities.Open, 1, new Dictionary<string, string> { ["kind"] = "document", ["target"] = "", ["delaySeconds"] = "0" }.ToImmutableDictionary()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        var compiler = new AutomationTaskCompiler(); compiler.Register(new SystemEventTaskDetection()); compiler.Register(new OpenTargetTaskAction());
        Assert.True(draft.Paused); Assert.Throws<ArgumentException>(() => compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var localPath = Path.Combine(Path.GetTempPath(), "SyntheticPlan.pdf");
        Assert.NotNull(compiler.Compile(draft with { Action = draft.Action with { Parameters = draft.Action.Parameters.SetItem("target", localPath) } }, "owner", ResourceScope.Session("owner", 1)));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = proposal.Action! with { Parameters = proposal.Action.Parameters.SetItem("target", localPath) } }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => compiler.Compile(draft with { Action = draft.Action with { Parameters = draft.Action.Parameters.SetItem("target", Path.ChangeExtension(localPath, ".cmd")) } }, "owner", ResourceScope.Session("owner", 1)));
    }
    [Fact]
    public void ImportedReminderRequiresManualTextButStillRejectsFractionalDelay()
    {
        var recorder = Recorder(); var catalog = Catalog();
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "定期提醒", "合成候选",
            new(IntervalCapabilities.Elapsed, 1, ImmutableDictionary<string, string>.Empty.Add("minutes", "30")),
            new(NotificationCapabilities.Show, 1, new Dictionary<string, string> { ["title"] = "", ["body"] = "", ["sound"] = "false", ["delaySeconds"] = "0" }.ToImmutableDictionary()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(2, UsageAnalysis.MissingManualFields(draft, catalog).Length);
        var compiler = new AutomationTaskCompiler(); compiler.Register(new IntervalTaskDetection()); compiler.Register(new NotificationTaskAction());
        Assert.Throws<ArgumentException>(() => compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        var completed = draft with { Action = draft.Action with { Parameters = draft.Action.Parameters.SetItem("title", "提醒").SetItem("body", "请休息片刻") } };
        Assert.NotNull(compiler.Compile(completed, "owner", ResourceScope.Session("owner", 1)));
        foreach (var (field, value) in new[] { ("body", "imported text"), ("title", "imported title"), ("delaySeconds", "0.5") })
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = proposal.Action! with { Parameters = proposal.Action.Parameters.SetItem(field, value) } }), recorder.Session, catalog));
    }
    [Fact]
    public void ImportedHdrCandidateCanLeaveDeviceSelectionForTheUserWithoutExposingIdentifiers()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "process-start", App("WorkTool"))); var catalog = Catalog();
        catalog = catalog with { Capabilities = catalog.Capabilities.Add(new HdrTaskAction().Descriptor), Combinations = catalog.Combinations.Add(new("process.running", "display.hdr")) };
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "HDR准备", "合成测试候选",
            new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", recorder.Session.Objects[0].Id)),
            new("display.hdr", 1, new Dictionary<string, string> { ["display"] = "", ["enabled"] = "true" }.ToImmutableDictionary()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(new[] { "显示器" }, UsageAnalysis.MissingManualFields(draft, catalog));
        Assert.Throws<ArgumentException>(() => GameHdrTasks.CreateCompiler().Compile(draft, "owner", ResourceScope.Session("owner", 1)));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = proposal.Action! with { Parameters = proposal.Action.Parameters.SetItem("display", "synthetic-untrusted-display") } }), recorder.Session, catalog));
    }

    [Fact]
    public void ImportedFileWatchCannotIntroduceAnUnobservedFilesystemPath()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "process-start", App("WorkTool")));
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "文件准备", "合成测试候选",
            new(FileArrivalCapabilities.Arrived, 1, new Dictionary<string, string> { ["directory"] = Path.GetTempPath(), ["kind"] = "pdf" }.ToImmutableDictionary()),
            new("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", recorder.Session.Objects[0].Id)));
        var error = Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, Catalog()));
        Assert.Contains("手工填写", error.Message);
    }

    [Fact]
    public void ImportedMicrophoneActionCannotToggleAndNeedsExplicitPausedReview()
    {
        var recorder = Recorder();
        var action = MicrophoneCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("operation", "toggle");
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "锁屏麦克风", "合成测试候选",
            new(SystemEventCapabilities.Event, 1, ImmutableDictionary<string, string>.Empty.Add("event", "locked")), new(MicrophoneCapabilities.Set, 1, action));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, Catalog()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = proposal.Action! with { Parameters = action.SetItem("operation", "mute") } }), recorder.Session, Catalog()).Draft!;
        Assert.True(draft.Paused); Assert.Equal(0, draft.Version); Assert.Equal("mute", MicrophoneCapabilities.Validate(draft.Action).Operation);
    }

    [Fact]
    public void ImportedPlaybackChangesRemainPausedAndRejectInvalidVolume()
    {
        var recorder = Recorder();
        var action = AudioPlaybackCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("operation", "volume").SetItem("percent", "101");
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "锁屏音量", "合成测试候选",
            new(SystemEventCapabilities.Event, 1, ImmutableDictionary<string, string>.Empty.Add("event", "locked")), new(AudioPlaybackCapabilities.Set, 1, action));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, Catalog()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = proposal.Action! with { Parameters = action.SetItem("percent", "30") } }), recorder.Session, Catalog()).Draft!;
        Assert.True(draft.Paused); Assert.Equal(0, draft.Version); Assert.Equal(30, AudioPlaybackCapabilities.Validate(draft.Action).Percent);
    }

    [Fact]
    public void ImportedOneTimeDateMustExistBeforeItBecomesAPausedDraft()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("WorkTool")));
        var parameters = new Dictionary<string, string> { ["year"] = "2027", ["month"] = "2", ["day"] = "29", ["hour"] = "9", ["minute"] = "0" }.ToImmutableDictionary();
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "单次准备", "合成测试候选",
            new(OnceTimeCapabilities.Due, 1, parameters),
            new("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", recorder.Session.Objects[0].Id)));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, Catalog()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = proposal.Detection! with { Parameters = parameters.SetItem("year", "2028") } }), recorder.Session, Catalog()).Draft!;
        Assert.True(draft.Paused); Assert.Equal("2028-02-29T09:00", OnceTimeCapabilities.Validate(draft.Detection).Resource);
    }

    [Fact]
    public void ImportedCalendarCannotOfferADraftWithNoSelectedDays()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("WorkTool")));
        var parameters = DailyTimeCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id, f => f.DefaultValue!);
        var noDays = parameters.ToBuilder(); for (var day = 0; day < 7; day++) noDays["day" + day] = "false";
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "定时准备", "合成测试候选",
            new(DailyTimeCapabilities.Due, 1, noDays.ToImmutable()),
            new("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", recorder.Session.Objects[0].Id)));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, Catalog()));
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = proposal.Detection! with { Parameters = parameters } }), recorder.Session, Catalog()).Draft!;
        Assert.True(draft.Paused); Assert.Equal(new DailyTimeSpec(9, 0, 62), DailyTimeCapabilities.Validate(draft.Detection));
    }

    [Fact]
    public void ImportedIntervalRequiresIntegerMinutesBeforeOfferingAPausedDraft()
    {
        var recorder = Recorder(); recorder.Add(new(Now, "foreground", App("Reminder")));
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "定时准备", "合成测试候选",
            new(IntervalCapabilities.Elapsed, 1, ImmutableDictionary<string, string>.Empty.Add("minutes", "1.5")),
            new("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).Add("executable", recorder.Session.Objects[0].Id)));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, Catalog()));
        var valid = proposal with { Detection = proposal.Detection! with { Parameters = proposal.Detection.Parameters.SetItem("minutes", "30") } };
        var draft = UsageAnalysis.Import(JsonSerializer.Serialize(valid), recorder.Session, Catalog()).Draft!;
        Assert.True(draft.Paused); Assert.Equal("30", draft.Detection.Parameters["minutes"]);
        Assert.Equal(App("Reminder"), draft.Action.Parameters["executable"]);
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
