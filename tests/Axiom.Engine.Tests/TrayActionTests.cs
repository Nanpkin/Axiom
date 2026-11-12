using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TrayActionTests
{
    public TrayActionTests() => IconWindow.CleanupHistoricalRegistrations();

    [Fact(Timeout = 15000)]
    public async Task HiddenRegistrationSurvivesOrdinaryUpdatesAndShowsWithoutOwnerRecreation()
    {
        using var target = new IconWindow(false, true);
        target.Add(false);
        var hidden = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), default);
        Assert.True(hidden.Success, hidden.Reason); Assert.True(hidden.TrayHiddenConfirmed); Assert.False(target.Exists(false));
        Assert.True(target.ProbeRegistration());
        Assert.True(target.ModifyTooltip());
        Assert.False(target.Exists(false)); Assert.True(target.ProbeRegistration());
        // This fixture does not implement TaskbarCreated: only clearing the retained
        // NIS_HIDDEN bit can restore its real icon and callback registration.
        var shown = await WindowsTrayActions.ShowAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), default);
        Assert.True(shown.Success, shown.Reason); Assert.True(target.Exists(false)); Assert.Equal(0, target.RecreationRequests);
        target.Remove(false);
        Assert.False(target.ProbeRegistration());
    }
    [Fact(Timeout = 15000)]
    public async Task HiddenStartupRegistrationWaitsForLaterVisibleIconBeforeCompleting()
    {
        using var target = new IconWindow(false, true);
        using var notifications = new WindowsTrayEvents();
        target.AddHidden();
        using var baseline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!target.RegisteredHidden()) await notifications.Events.Reader.ReadAsync(baseline.Token);
        var pending = WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(3), default);
        await Task.Delay(150); Assert.False(pending.IsCompleted);
        // Cold-start frameworks can retain the initial hidden registration and create a
        // different visible UID later. Only the later visible icon completes this command.
        target.Add(true);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(result.Success, result.Reason); Assert.False(result.Skipped); Assert.True(result.TrayHiddenConfirmed);
        Assert.False(target.Exists(false)); Assert.False(target.Exists(true));
        Assert.True(target.ProbeRegistration()); Assert.True(target.ProbeRegistration(true));
    }
    [Fact(Timeout = 15000)]
    public async Task StartupHideWaitsForReplacementRegistrationToSettle()
    {
        using var initial = new IconWindow(false, true);
        using var replacement = new IconWindow(false, true);
        initial.Add(false);
        Assert.True(initial.Exists(false));

        var pending = WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(6), default,
            registrationQuietPeriod: TimeSpan.FromSeconds(1));
        Assert.False(pending.IsCompleted);
        Assert.True(initial.Exists(false));
        initial.Remove(false);
        replacement.Add(false);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(7));
        Assert.True(result.Success, result.Reason);
        Assert.True(result.TrayHiddenConfirmed);
        Assert.True(replacement.ProbeRegistration());
        Assert.False(replacement.Exists(false));
    }
    [Fact]
    public void TrayModuleUsesStartupComposerAndBatchPathsWithDelayAndStrictValidation()
    {
        var modules = new IAutomationPlugin[] { new ProgramPlugin(),new AxiomStartupPlugin(),new TrayPlugin() };
        var compiler=new AutomationTaskCompiler();
        foreach(var module in modules) {foreach(var detection in module.Detections)compiler.Register(detection);foreach(var action in module.Actions)compiler.Register(action);}
        var combinations=modules.SelectMany(m=>m.Combinations).ToImmutableArray(); var editor=new TaskEditorState();editor.Load(null);editor.Title="启动隐藏托盘";
        editor.Select(new AxiomStartupTaskDetection().Descriptor,combinations);editor.SetParameter(TaskCapabilityKind.Detection,"frequency","firstBoot");
        editor.Select(TrayCapabilities.Descriptor,combinations);editor.SetParameter(TaskCapabilityKind.Action,"executable","C:\\Apps\\SampleAudio\\SampleAudio.exe\nC:\\Tools\\Other.exe\nC:\\Apps\\SampleAudio\\SampleAudio.exe");
        editor.SetParameter(TaskCapabilityKind.Action,"delaySeconds","2");
        var task=editor.Build();var rule=compiler.Compile(task,"owner",ResourceScope.Session("owner",1));
        Assert.Equal(RuleActionKind.Command,rule.Action.Kind);Assert.Equal(TrayCapabilities.Hide,rule.Action.Key.CapabilityId);Assert.Equal(TimeSpan.FromSeconds(2),rule.Timing.MinimumDuration);
        Assert.Equal(2,TrayCapabilities.ValidateTargets(task.Action).Length);
        Assert.Equal(TaskFieldKind.TraySelection,TrayCapabilities.Descriptor.Fields.Single(f=>f.Id=="executable").Kind);
        foreach(var value in new[]{"0","121","NaN"})Assert.Throws<ArgumentException>(()=>TrayCapabilities.ValidateTargets(task.Action with{Parameters=task.Action.Parameters.SetItem("waitSeconds",value)}));
        foreach(var value in new[]{"SampleAudio.exe",@"C:\Windows\cmd.exe",@"C:\Axiom.exe"})Assert.Throws<ArgumentException>(()=>TrayCapabilities.ValidateTargets(task.Action with{Parameters=task.Action.Parameters.SetItem("executable",value)}));
        Assert.Throws<ArgumentException>(()=>TrayCapabilities.ValidateTargets(task.Action with{Parameters=task.Action.Parameters.Add("inject","true")}));
    }
    [Fact]
    public void ShowCompilesAsBoundedCommandAndValidatesProviderDeadline()
    {
        var config = new TaskInvocation(TrayCapabilities.Show, 1, ImmutableDictionary<string, string>.Empty
            .Add("executable", @"C:\Apps\TraySample.exe").Add("waitSeconds", "120").Add("delaySeconds", "2"));
        var compiler = new ShowTrayTaskAction(); var scope = ResourceScope.Session("owner", 1);
        var action = compiler.Compile(config, scope);
        Assert.Equal(TrayCapabilities.Show, action.Key.CapabilityId); Assert.Equal(RuleActionKind.Command, action.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), compiler.Timing(config).MinimumDuration);
        var plugin = new TrayPlugin();
        Assert.Contains(plugin.Actions, a => a.Descriptor.Id == TrayCapabilities.Show);
        Assert.Contains(new("process.running", TrayCapabilities.Show), plugin.Combinations);
        Assert.Contains(plugin.Bindings, b => b.Descriptor.Id == TrayCapabilities.Show);
        var now = DateTimeOffset.UtcNow;
        var request = new ProviderMessage(1, "invoke", "show-tray", now.AddSeconds(130), Invocation: config, CommandId: Guid.NewGuid());
        ProviderProtocol.Validate(request, now);
        Assert.Throws<ArgumentException>(() => ProviderProtocol.Validate(request with
        { Invocation = config with { Parameters = config.Parameters.SetItem("waitSeconds", "121") } }, now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(request with { Type = "observe-tray", CommandId = null }, now));
        Assert.Throws<ArgumentException>(() => compiler.Compile(config with { Parameters = config.Parameters.SetItem("executable", "TraySample.exe") }, scope));
    }
    [Fact(Timeout = 15000)]
    public async Task ShowClearsHiddenRegistrationAndOnlyRecreatesMissingIcon()
    {
        using var target = new IconWindow(true, true, true);
        target.Add(false);
        Assert.True((await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), default)).Success);
        Assert.False(target.Exists(false));
        var shown = await WindowsTrayActions.ShowAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(3), default);
        Assert.True(shown.Success, shown.Reason); Assert.False(shown.Skipped);
        Assert.True(target.Exists(false)); Assert.True(target.Alive); Assert.Equal(0, target.RecreationRequests);
        var already = await WindowsTrayActions.ShowAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.True(already.Success); Assert.True(already.Skipped); Assert.Equal(0, target.RecreationRequests);
        target.Remove(false);
        var recreated = await WindowsTrayActions.ShowAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(3), default);
        Assert.True(recreated.Success, recreated.Reason); Assert.True(target.Exists(false)); Assert.Equal(1, target.RecreationRequests);
    }
    [Fact(Timeout = 15000)]
    public async Task ShowCannotClaimSuccessForUnsupportedProgramAndCancellationPreventsRequests()
    {
        using var target = new IconWindow(false, true);
        var unsupported = await WindowsTrayActions.ShowAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.False(unsupported.Success); Assert.Contains("未确认显示", unsupported.Reason); Assert.False(target.Exists(false));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsTrayActions.ShowAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), cancelled.Token));
        var missing = await WindowsTrayActions.ShowAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), TimeSpan.FromSeconds(1), default);
        Assert.False(missing.Success); Assert.True(target.Alive);
    }
    private static IReadOnlyDictionary<string, string> RestoreParameters() => new Dictionary<string, string>
    { ["executable"] = Environment.ProcessPath!, ["delaySeconds"] = "0" };

    [Fact(Timeout = 15000)]
    public async Task VisibilityLeaseShowsHiddenIconAndRestoresFirstHiddenBaseline()
    {
        using var target = new IconWindow(true, true, true, recreateOwner: true);
        await using var lease = await WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, default);
        var applied = await lease.ApplyAsync(RestoreParameters(), default);
        Assert.True(applied.Success, applied.Reason); Assert.True(applied.Owned); Assert.True(target.Exists(false));
        // An in-place desired value refresh must not replace the first hidden baseline.
        var repeated = await lease.ApplyAsync(RestoreParameters(), default);
        Assert.True(repeated.Success, repeated.Reason); Assert.Equal(1, target.RecreationRequests);
        var restored = await lease.ReleaseAsync(default);
        Assert.True(restored.Success, restored.Reason); Assert.False(restored.Owned); Assert.False(target.Exists(false)); Assert.True(target.Alive);
        Assert.True(target.ProbeRegistration()); Assert.True(target.ModifyTooltip()); Assert.False(target.Exists(false));
        await using var retained = await WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, default);
        Assert.True((await retained.ApplyAsync(RestoreParameters(), default)).Success); Assert.True(target.Exists(false));
        Assert.Equal(1, target.RecreationRequests);
        Assert.True((await retained.ReleaseAsync(default)).Success); Assert.False(target.Exists(false)); Assert.True(target.ProbeRegistration());
    }
    [Fact(Timeout = 15000)]
    public async Task VisibilityLeasePreservesOriginallyVisibleTrayAndRestoresSameProcessNewIcon()
    {
        using var target = new IconWindow(false, true, true);
        target.Add(false);
        await using (var originalVisible = await WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, default))
        {
            Assert.True((await originalVisible.ApplyAsync(RestoreParameters(), default)).Success);
            Assert.True((await originalVisible.ReleaseAsync(default)).Success); Assert.True(target.Exists(false));
            Assert.Equal(0, target.RecreationRequests);
        }
        target.Remove(false);
        await using var changed = await WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, default);
        Assert.True((await changed.ApplyAsync(RestoreParameters(), default)).Success);
        target.Remove(false); target.Add(true);
        var released = await changed.ReleaseAsync(default);
        Assert.True(released.Success, released.Reason);
        Assert.False(target.Exists(true)); Assert.False(target.Exists(false));
        await using var externallyHidden = await WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, default);
        Assert.True((await externallyHidden.ApplyAsync(RestoreParameters(), default)).Success);
        target.Remove(true);
        var preserved = await externallyHidden.ReleaseAsync(default);
        Assert.True(preserved.Success, preserved.Reason); Assert.Contains("其他来源", preserved.Reason);
        Assert.False(target.Exists(false)); Assert.False(target.Exists(true));
    }
    [Fact(Timeout = 15000)]
    public async Task VisibilityLeaseRejectsMissingProcessAndAcceptsNewOwnerWithinPinnedProcess()
    {
        await Assert.ThrowsAsync<RestorableSettingNotAcquiredException>(() => WindowsTrayActions.CreateRestorableSessionAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), default));
        var target = new IconWindow(true, true, true);
        await using var lease = await WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, default);
        Assert.True((await lease.ApplyAsync(RestoreParameters(), default)).Success);
        target.Dispose();
        using var replacement = new IconWindow(true, true, true); replacement.Add(false);
        var released = await lease.ReleaseAsync(default);
        Assert.True(released.Success, released.Reason); Assert.False(replacement.Exists(false)); Assert.True(replacement.Alive);
    }
    [Fact(Timeout = 15000)]
    public async Task VisibilityLeaseDoesNotRestoreUnconfirmedWritesAndCancelledCaptureDoesNotWrite()
    {
        using var target = new IconWindow(true, true);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<RestorableSettingNotAcquiredException>(() =>
            WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, cancelled.Token));
        Assert.False(target.Exists(false)); Assert.Equal(0, target.RecreationRequests);
        await using var lease = await WindowsTrayActions.CreateRestorableSessionAsync(Environment.ProcessPath!, default);
        var failed = await lease.ApplyAsync(RestoreParameters(), default);
        Assert.False(failed.Success); Assert.False(failed.Owned);
        // A late program-created icon is not an Axiom-confirmed write and must survive release.
        target.Add(false);
        var released = await lease.ReleaseAsync(default);
        Assert.True(released.Success, released.Reason); Assert.True(target.Exists(false));
    }
    [Fact(Timeout=15000)]
    public async Task CurrentCatalogIncludesDynamicWindowUidAndDropsRemovedIcons()
    {
        using var target = new IconWindow(false, true);
        target.Add(false);
        var catalog = WindowsTrayActions.ReadCatalog(default);
        var entry = Assert.Single(catalog.Entries, e => e.ExecutablePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, entry.IconCount);
        Assert.True(target.Exists(false)); // Discovery has no side effects.
        var result = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), default);
        Assert.True(result.Success, result.Reason);
        Assert.False(target.Exists(false)); Assert.True(target.Alive);
        Assert.DoesNotContain(WindowsTrayActions.ReadCatalog(default).Entries, e => e.ExecutablePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        target.Remove(false); target.Add(false); Assert.True(target.Exists(false));
    }
    [Fact]
    public async Task CatalogRequestsAreReadOnlyAndBoundedAndSelectionCancelIsIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new AutomationMessage(1, "read-tray", "catalog", now.AddSeconds(10));
        AutomationProtocol.ValidateRequest(request, now);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Id = Guid.NewGuid().ToString() }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Tray = new([], now) }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request, now, true));
        var provider = new ProviderMessage(1, "read-tray", "catalog", now.AddSeconds(10)); ProviderProtocol.Validate(provider, now);
        using var frame = new MemoryStream(); await AutomationProtocol.WriteAsync(frame, request, default);
        Assert.DoesNotContain("\"Tray\"", System.Text.Encoding.UTF8.GetString(frame.ToArray()));
        using var providerFrame = new MemoryStream(); await ProviderProtocol.WriteAsync(providerFrame, provider, default);
        Assert.DoesNotContain("\"Tray\"", System.Text.Encoding.UTF8.GetString(providerFrame.ToArray()));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(provider with { CommandId = Guid.NewGuid() }, now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(provider with { Resources = [] }, now));
        TrayCatalogValidation.Validate(new([new(@"C:\Apps\bin\SampleApp.exe", "SampleApp", 2)], now));
        Assert.Throws<ArgumentException>(() => TrayCatalogValidation.Validate(new([new("SampleApp.exe", "SampleApp", 1)], now)));
        Assert.Throws<InvalidDataException>(() => TrayCatalogValidation.Validate(new([new(@"C:\Apps\One.exe", "One", 0)], now)));
        Assert.Throws<InvalidDataException>(() => TrayCatalogValidation.Validate(new([new(@"C:\Apps\One.exe", "One", 1), new(@"c:\apps\one.exe", "One", 1)], now)));
        var original = new ProcessSelectionState(@"C:\Apps\Existing.exe", true); var draft = new ProcessSelectionState(original.Value, true);
        draft.Set([@"C:\Apps\bin\SampleApp.exe", @"c:\apps\bin\sampleapp.exe"], true);
        Assert.Equal(2, draft.Names.Count); Assert.Single(original.Names);
    }
    [Fact(Timeout=15000)]
    public async Task HidesIdAndGuidIconsIncludingMessageOnlyWindowWithoutRemovingRegistration()
    {
        using var first=new IconWindow(false, true);using var second=new IconWindow(true, true);
        first.Add(false);first.Add(true);second.Add(false);
        Assert.True(first.Exists(false));Assert.True(first.Exists(true));Assert.True(second.Exists(false));
        var result=await WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(3),default);
        Assert.True(result.Success,result.Reason);Assert.Contains("3 个",result.Reason);
        Assert.False(first.Exists(false));Assert.False(first.Exists(true));Assert.False(second.Exists(false));
        Assert.True(first.ProbeRegistration()); Assert.True(first.ProbeRegistration(true)); Assert.True(second.ProbeRegistration());
        Assert.True(first.Alive);Assert.True(second.Alive);
        first.Remove(false);first.Add(false);Assert.True(first.Exists(false)); // Explicit deletion/recreation is not continuously suppressed.
    }
    [Fact(Timeout=15000)]
    public async Task WaitsForIconOnExistingWindowUsingNotificationsAndCancellationPreventsLaterDeletion()
    {
        using var target=new IconWindow(false);
        var pending=WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(4),default);
        await Task.Delay(1200); Assert.False(pending.IsCompleted);
        target.Add(false);Assert.True((await pending).Success);
        using var cancel=new CancellationTokenSource();
        var cancelled=WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(4),cancel.Token);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);
        target.Remove(false);target.Add(false);Assert.True(target.Exists(false));
    }
    [Fact(Timeout=15000)]
    public async Task NoIconIsNoOpAndMissingPathDoesNotAffectOtherIcons()
    {
        using var target=new IconWindow(false, true);
        var noIcon=await WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(1),default);
        Assert.True(noIcon.Success);Assert.True(noIcon.Skipped);Assert.Contains("无需处理",noIcon.Reason);
        Assert.False(noIcon.TrayHiddenConfirmed);
        Assert.DoesNotContain("已隐藏成功",noIcon.Reason);
        target.Add(false);
        var missing=await WindowsTrayActions.HideAsync(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".exe"),TimeSpan.FromSeconds(1),default);
        Assert.False(missing.Success);Assert.True(target.Exists(false));
        var hidden = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.True(hidden.Success); Assert.False(hidden.Skipped); Assert.True(hidden.TrayHiddenConfirmed);
        var repeated = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.True(repeated.Success); Assert.True(repeated.Skipped); Assert.True(repeated.TrayHiddenConfirmed); Assert.True(target.Alive);
    }
    [Fact(Timeout = 15000)]
    public async Task ShortStartupWaitMissesLateWindowButLongWaitHidesItWithoutManualRetry()
    {
        var shortWait = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.True(shortWait.Success); Assert.True(shortWait.Skipped);
        var pending = WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(60), default);
        await Task.Delay(1500); Assert.False(pending.IsCompleted);
        using var target = new IconWindow(false, true);
        target.Add(false);
        var hidden = await pending.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(hidden.Success, hidden.Reason); Assert.False(target.Exists(false)); Assert.True(target.Alive);
        using var cancel = new CancellationTokenSource();
        var cancelled = WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(120), cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        target.Remove(false); target.Add(false); Assert.True(target.Exists(false));
    }
    [Fact(Timeout=15000)]
    public async Task SharedWaitSourceSurvivesOneSubscriberAndCanBeRecreated()
    {
        using var one=new WindowsTrayEvents();
        // Exercise lease delivery deterministically. Explorer need not emit a WinEvent for removal
        // of an overflow icon, and its historical registry entry may remain unchanged.
        using(var two=new WindowsTrayEvents()) { WindowsTrayEvents.Invalidate();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));Assert.True(await two.Events.Reader.ReadAsync(deadline.Token)); }
        Assert.True(one.Events.Reader.TryRead(out _));
        WindowsTrayEvents.Invalidate();using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(3));Assert.True(await one.Events.Reader.ReadAsync(limit.Token));
        one.Dispose();
        using var recreated=new WindowsTrayEvents();WindowsTrayEvents.Invalidate();using var end=new CancellationTokenSource(TimeSpan.FromSeconds(3));Assert.True(await recreated.Events.Reader.ReadAsync(end.Token));
    }
    [Fact(Timeout = 15000)]
    public async Task SmallLiveUidIsRecognizedWithoutTrustingHistoricalRegistration()
    {
        using var target = new IconWindow(true, recreate: true, fixedUid: 2);
        target.Add(false);
        Assert.True(target.Exists(false));
        var hidden = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), default);
        Assert.True(hidden.Success, hidden.Reason); Assert.False(hidden.Skipped); Assert.False(target.Exists(false));
        var shown = await WindowsTrayActions.ShowAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), default);
        Assert.True(shown.Success, shown.Reason); Assert.True(target.Exists(false)); Assert.True(target.Alive);
    }
    [Fact(Timeout = 15000)]
    public async Task ContinuousTargetNotificationsHideSameIdentityAgainAndCancelLeavesNewIconVisible()
    {
        using var target = new IconWindow(false, true);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opportunities = System.Threading.Channels.Channel.CreateBounded<long>(1);
        var completions = System.Threading.Channels.Channel.CreateUnbounded<ProgramControlResult>();
        var watcher = WindowsTrayActions.WatchTargetsAsync([Environment.ProcessPath!], (snapshot, counter, active) =>
        {
            if (snapshot) ready.TrySetResult(); else if (active) opportunities.Writer.TryWrite(counter);
        }, lifetime.Token);
        var worker = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                await opportunities.Reader.ReadAsync(lifetime.Token);
                var result = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), lifetime.Token);
                completions.Writer.TryWrite(result);
            }
        }, lifetime.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            target.Add(false);
            var first = await completions.Reader.ReadAsync(lifetime.Token); Assert.True(first.Success, first.Reason); Assert.False(target.Exists(false));
            // NIM_ADD reuses the same HWND/UID, including when removal and creation notifications merge.
            target.Remove(false); target.Add(false);
            var second = await completions.Reader.ReadAsync(lifetime.Token); Assert.True(second.Success, second.Reason); Assert.False(target.Exists(false));
        }
        finally
        {
            lifetime.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
            try { await worker; } catch (OperationCanceledException) { }
        }
        target.Remove(false); target.Add(false); Assert.True(target.Exists(false)); Assert.True(target.Alive);
    }
    [Fact]
    public async Task BatchSummariesSeparateAbsentTargetsFromFailuresAndCollapseRepeatedReasons()
    {
        var paths = Enumerable.Range(1, 8).Select(i => $@"C:\Synthetic\Tray{i}.exe").ToArray();
        var absent = await WindowsProgramBatch.ExecuteAsync(paths,
            (_, _) => Task.FromResult(new ProgramControlResult(true, "未发现图标，无需处理。") { Skipped = true }), default);
        Assert.True(absent.Success); Assert.True(absent.Skipped);
        Assert.Contains("无需处理 8 个目标", absent.Reason); Assert.DoesNotContain("0/8", absent.Reason);
        Assert.DoesNotContain(paths[0], absent.Reason.Split('\n')[0]);
        Assert.All(paths, path => Assert.Contains(path, absent.Reason));
        var mixed = await WindowsProgramBatch.ExecuteAsync(paths, (path, _) => Task.FromResult(path == paths[0]
            ? new ProgramControlResult(false, "权限不足") : new ProgramControlResult(true, "未发现图标") { Skipped = true }), default);
        Assert.False(mixed.Success); Assert.Contains("失败 1 个", mixed.Reason); Assert.Contains("无需处理 7 个", mixed.Reason);
        Assert.Contains(paths[0], mixed.Reason); Assert.Contains(paths[1], mixed.Reason);
        Assert.Contains("【失败】权限不足", mixed.Reason); Assert.Contains("【无需处理】", mixed.Reason);
        var denied = await WindowsProgramBatch.ExecuteAsync(paths,
            (_, _) => Task.FromResult(new ProgramControlResult(false, "权限不足")), default);
        Assert.False(denied.Success); Assert.Equal(1, denied.Reason.Split("权限不足").Length - 1);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsProgramBatch.ExecuteAsync(paths,
            (_, _) => Task.FromResult(new ProgramControlResult(true, "不应调用")), cancelled.Token));
    }

    private sealed class IconWindow : IDisposable
    {
        private static int serial;
        private uint id=(uint)Interlocked.Increment(ref serial)+20000;
        private readonly Guid guid=Guid.NewGuid();
        private readonly Thread thread;
        private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private readonly HashSet<uint> issuedIds = [];
        private bool issuedGuid;
        private System.Runtime.ExceptionServices.ExceptionDispatchInfo? workerFailure;
        private bool disposed;
        private nint window;private uint threadId;
        internal int RecreationRequests;
        internal string Diagnostic => $"Window={window},Id={id},Guid={guid}";
        internal IconWindow(bool messageOnly, bool dynamicUid = false, bool recreate = false, uint? fixedUid = null, bool recreateOwner = false)
        {
            if (fixedUid.HasValue) id = fixedUid.Value;
            thread=new Thread(()=>{
                try
                {
                    threadId=GetCurrentThreadId();
                    window=CreateWindowEx(0,"STATIC","Axiom tray test",0,0,0,0,0,messageOnly?-3:0,0,0,0);
                    if (dynamicUid) id = unchecked((uint)window.ToInt64());
                    if(window==0) throw new InvalidOperationException("Fixture window failed");
                    var taskbarCreated = RegisterWindowMessage("TaskbarCreated");
                    if (taskbarCreated == 0) throw new InvalidOperationException("Fixture recreation message registration failed");
                    ready.TrySetResult();
                    while(GetMessage(out var msg,0,0,0)>0)
                    {
                        if (msg.Id == taskbarCreated && recreate)
                        {
                            lock (gate)
                            {
                                if (disposed) continue;
                                Interlocked.Increment(ref RecreationRequests);
                                // A queued recreation request can arrive after the icon is already
                                // recreated. It must not issue a duplicate NIM_ADD for that identity.
                                if (!ExistsCore(false))
                                {
                                    if (recreateOwner)
                                    {
                                        var previous = window;
                                        var replacement = CreateWindowEx(0,"STATIC","Axiom tray recreated owner",0,0,0,0,0,messageOnly?-3:0,0,0,0);
                                        Assert.NotEqual(nint.Zero, replacement);
                                        RemoveCore(false); RemoveCore(true);
                                        window = replacement;
                                        if (dynamicUid) id = unchecked((uint)window.ToInt64());
                                        Assert.True(DestroyWindow(previous), "Fixture previous owner could not be destroyed");
                                    }
                                    AddCore(false);
                                }
                            }
                        }
                        TranslateMessage(ref msg);DispatchMessage(ref msg);
                    }
                }
                catch (Exception error)
                {
                    lock (gate) workerFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                    ready.TrySetException(error);
                }
                finally
                {
                    lock (gate)
                    {
                        if (window != 0) { RemoveCore(false); RemoveCore(true); DestroyWindow(window); window = 0; }
                    }
                }
            }){IsBackground=true};thread.Start();ready.Task.GetAwaiter().GetResult();
        }
        private void CheckWorker() => workerFailure?.Throw();
        internal bool Alive { get { lock (gate) { CheckWorker(); return IsWindow(window); } } }
        private IconData Data(bool withGuid)=>new(){Size=(uint)Marshal.SizeOf<IconData>(),Window=window,Id=id+(withGuid?1u:0u),Flags=6u|(withGuid?32u:0u),Icon=LoadIcon(0,32512),Tip="Axiom tray test "+id,Info="",Title="",Guid=withGuid?guid:Guid.Empty};
        private void AddCore(bool withGuid, bool hidden = false)
        {
            var data=Data(withGuid);
            issuedIds.Add(data.Id);
            issuedGuid |= withGuid;
            if(hidden) { data.Flags |= 8u; data.State = 1; data.Mask = 1; }
            Assert.True(Shell_NotifyIcon(0,ref data),"Fixture icon could not be created on desktop");
        }
        private void RemoveCore(bool withGuid){var data=Data(withGuid);Shell_NotifyIcon(2,ref data);}
        private bool ExistsCore(bool withGuid){var data=Data(withGuid);var identity=new Identifier{Size=(uint)Marshal.SizeOf<Identifier>(),Window=window,Id=data.Id,Guid=data.Guid};return Shell_NotifyIconGetRect(ref identity,out var rect)==0 && rect.Right>rect.Left && rect.Bottom>rect.Top;}
        internal void Add(bool withGuid){lock(gate){CheckWorker();Assert.False(disposed);AddCore(withGuid);}}
        internal void Remove(bool withGuid){lock(gate){CheckWorker();Assert.False(disposed);RemoveCore(withGuid);}}
        internal bool Exists(bool withGuid){lock(gate){CheckWorker();return ExistsCore(withGuid);}}
        internal void AddHidden(bool withGuid = false)
        {
            lock (gate) { CheckWorker(); Assert.False(disposed); AddCore(withGuid, true); }
        }
        internal bool RegisteredHidden()
        {
            lock(gate)
            {
                CheckWorker(); var data=Data(false);
                var identity=new Identifier{Size=(uint)Marshal.SizeOf<Identifier>(),Window=window,Id=data.Id,Guid=data.Guid};
                return Shell_NotifyIconGetRect(ref identity,out var rect)==1 && rect.Left==0 && rect.Top==0 && rect.Right==0 && rect.Bottom==0;
            }
        }
        internal bool ModifyTooltip()
        {
            lock (gate) { CheckWorker(); var data = Data(false); data.Flags = 4; data.Tip = "Axiom updated fixture icon"; return Shell_NotifyIcon(1, ref data); }
        }
        internal bool ProbeRegistration(bool withGuid = false)
        {
            lock (gate) { CheckWorker(); var data = Data(withGuid); data.Flags = withGuid ? 32u : 0u; return Shell_NotifyIcon(1, ref data); }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (!disposed) { disposed = true; PostThreadMessage(threadId,0x0012,0,0); }
            }
            Assert.True(thread.Join(TimeSpan.FromSeconds(3)), "Fixture owner thread did not stop");
            if (issuedGuid)
            {
                // A GUID remains globally addressable after its fixture HWND is destroyed.
                // Remove only this instance's generated identity, independently of the old
                // owner window, and prove it is no longer registered before the next test.
                var data=Data(true); data.Window=0; data.Id=0; data.Flags=32;
                Shell_NotifyIcon(2,ref data);
                Assert.False(Shell_NotifyIcon(1,ref data), "Fixture GUID registration survived final cleanup");
            }
            CleanupRegistrations(issuedIds, guid);
            lock (gate) CheckWorker();
        }
        internal static void CleanupHistoricalRegistrations() => CleanupRegistrations(null, null);
        private static void CleanupRegistrations(HashSet<uint>? ownedIds, Guid? ownedGuid)
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                bool remove;
                using (var key = root.OpenSubKey(name))
                {
                    if (key is null || key.GetValue("ExecutablePath") is not string path ||
                        !path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var tip = key.GetValue("InitialTooltip") as string;
                    var fixtureTip = tip?.StartsWith("Axiom tray test ", StringComparison.Ordinal) == true || tip == "Axiom updated fixture icon";
                    var uid = key.GetValue("UID") is int number ? unchecked((uint)number) : (uint?)null;
                    var rawGuid = key.GetValue("IconGuid");
                    var iconGuid = rawGuid is byte[] { Length: 16 } bytes ? new Guid(bytes) :
                        rawGuid is string text && Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;
                    // Historical cleanup requires this fixture's explicit tooltip marker. Empty
                    // tooltips are never assumed ours; only this instance's generated GUID can
                    // establish ownership of its otherwise unlabelled GUID registration.
                    remove = ownedIds is null ? fixtureTip :
                        fixtureTip && uid.HasValue && ownedIds.Contains(uid.Value) || ownedGuid.HasValue && iconGuid == ownedGuid.Value;
                }
                if (remove) root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            }
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct IconData{
            public uint Size;public nint Window;public uint Id,Flags,Message;public nint Icon;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;public uint State,Mask;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Title;public uint InfoFlags;public Guid Guid;public nint Balloon;}
        [StructLayout(LayoutKind.Sequential)] private struct Identifier{public uint Size;public nint Window;public uint Id;public Guid Guid;}
        [StructLayout(LayoutKind.Sequential)] private struct Rect{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)]private struct Msg{public nint Window;public uint Id;public nint W,L;public uint Time;public int X,Y;public uint Private;}
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint parameter);
        [DllImport("user32.dll")]private static extern nint LoadIcon(nint instance,nint name);
        [DllImport("user32.dll")]private static extern int GetMessage(out Msg message,nint window,uint min,uint max);
        [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Msg message);
        [DllImport("user32.dll")]private static extern nint DispatchMessage(ref Msg message);
        [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")]private static extern bool IsWindow(nint window);
        [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint thread,uint message,nint w,nint l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string name);
        [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint operation,ref IconData data);
        [DllImport("shell32.dll")]private static extern int Shell_NotifyIconGetRect(ref Identifier id,out Rect rect);
    }
}
