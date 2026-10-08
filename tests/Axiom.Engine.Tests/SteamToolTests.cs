using Axiom.Contracts;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class SteamToolTests
{
    private static SteamToolScan Scan(params string[] names) => new(names.Select(n => new ProcessGroupEntry(n, "Game " + n)).ToArray(), names.Length, [], true);
    [Fact]
    public async Task DisabledStartupDoesNotReadSteamAndEnabledRestartKeepsIdentityAndExclusions()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"); var count = 0; string groupId;
        try
        {
            using (var store = new SqliteProcessGroupStore(path))
            using (var manager = new SteamToolManager(store, (_, _) => { count++; return Task.FromResult(Scan("one.exe", "two.exe")); }))
            {
                await manager.ScanAsync(true, true, default); Assert.Equal(0, count); Assert.Empty(manager.ReadGroups());
                var result = await manager.SaveAsync(new(0, true, ["TWO.EXE"]), default); Assert.Equal(1, count);
                var group = Assert.Single(manager.ReadGroups()); groupId = group.Id; Assert.True(group.AutoManaged);
                Assert.Equal(SteamTool.GroupName, group.Name); Assert.Equal("one.exe", Assert.Single(group.Entries).ProcessName);
                Assert.Equal(2, result.DetectedCount); Assert.Equal(1, result.ExportedCount);
                for (var i = 0; i < 10; i++) manager.Read(); Assert.Equal(1, count);
            }
            using (var store = new SqliteProcessGroupStore(path))
            using (var manager = new SteamToolManager(store, (_, _) => { count++; return Task.FromResult(Scan("one.exe", "two.exe", "three.exe")); }))
            {
                var before = manager.Read(); Assert.True(before.Settings.Enabled); Assert.Equal("two.exe", Assert.Single(before.Settings.ExcludedProcesses));
                await manager.ScanAsync(true, true, default); var group = Assert.Single(manager.ReadGroups()); Assert.Equal(groupId, group.Id);
                Assert.Equal(new[] { "one.exe", "three.exe" }, group.Entries.Select(e => e.ProcessName));
            }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task CompleteUpdatesReplaceInstalledSetButUnchangedDoesNotIncreaseVersionAndOtherGroupsStay()
    {
        using var f = new Fixture(); var scan = Scan("old.exe", "keep.exe");
        using var manager = new SteamToolManager(f.Store, (_, _) => Task.FromResult(scan));
        var other = f.Store.Save(new(Guid.NewGuid().ToString("D"), 0, "用户组", [new("custom.exe", "Custom")]));
        await manager.SaveAsync(new(0, true, []), default); var first = manager.ReadGroups().Single(g => g.AutoManaged);
        await manager.ScanAsync(true, true, default); Assert.Equal(first.Version, manager.ReadGroups().Single(g => g.AutoManaged).Version);
        scan = Scan("keep.exe", "new.exe"); await manager.ScanAsync(true, true, default);
        var changed = manager.ReadGroups().Single(g => g.AutoManaged); Assert.Equal(first.Id, changed.Id); Assert.Equal(first.Version + 1, changed.Version);
        Assert.Equal(new[] { "keep.exe", "new.exe" }, changed.Entries.Select(e => e.ProcessName));
        AssertSameGroup(other, manager.ReadGroups().Single(g => !g.AutoManaged));
        await manager.ScanAsync(true, true, default); Assert.Throws<InvalidOperationException>(() => manager.SaveGroup(changed));
    }
    [Fact]
    public async Task MissingMetadataFailureAndCancellationPreserveGroupAndDisabledManualScanDoesNotExport()
    {
        using var f = new Fixture(); var scan = Scan("one.exe"); var fail = false;
        using var manager = new SteamToolManager(f.Store, (_, token) => { token.ThrowIfCancellationRequested(); if (fail) throw new IOException("库暂不可用"); return Task.FromResult(scan); });
        await manager.SaveAsync(new(0, true, []), default); var first = Assert.Single(manager.ReadGroups());
        fail = true; var failed = await manager.ScanAsync(true, true, default); Assert.Contains("库暂不可用", failed.Error); AssertSameGroup(first, Assert.Single(manager.ReadGroups()));
        fail = false; scan = Scan() with { Complete = false, Warnings = ["缺少元数据"] };
        Assert.NotNull((await manager.ScanAsync(true, true, default)).Error); AssertSameGroup(first, Assert.Single(manager.ReadGroups()));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ScanAsync(true, true, cancel.Token));
        await manager.SaveAsync(manager.Read().Settings with { Enabled = false }, default); scan = Scan("new.exe");
        var manual = await manager.ScanAsync(true, false, default); Assert.Equal("new.exe", Assert.Single(manual.Processes).ProcessName); AssertSameGroup(first, Assert.Single(manager.ReadGroups()));
    }
    [Fact]
    public async Task ExclusionsApplyImmediatelyEvenWhenNextScanFailsAndStaleSettingsCannotOverwrite()
    {
        using var f = new Fixture(); var fail = false;
        using var manager = new SteamToolManager(f.Store, (_, _) => fail ? throw new IOException("offline") : Task.FromResult(Scan("one.exe", "two.exe")));
        await manager.SaveAsync(new(0, true, []), default); var settings = manager.Read().Settings; fail = true;
        await manager.SaveAsync(settings with { ExcludedProcesses = ["two.exe", "future.exe"] }, default);
        Assert.Equal("one.exe", Assert.Single(Assert.Single(manager.ReadGroups()).Entries).ProcessName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveAsync(settings with { Enabled = false }, default));
        Assert.Equal(2, manager.Read().Settings.ExcludedProcesses.Length);
    }
    [Fact]
    public async Task DisableDuringPendingScanPreventsExportAndScansAreSerialized()
    {
        using var f = new Fixture(); var complete = new TaskCompletionSource<SteamToolScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new SteamToolManager(f.Store, (_, _) => complete.Task);
        var enable = manager.SaveAsync(new(0, true, []), default); Assert.True(manager.Read().Scanning);
        await manager.SaveAsync(manager.Read().Settings with { Enabled = false }, default); complete.SetResult(Scan("one.exe")); await enable;
        Assert.Empty(manager.ReadGroups()); Assert.False(manager.Read().Settings.Enabled); Assert.False(manager.Read().Scanning);
    }
    [Fact]
    public async Task OverCapacityKeepsGroupAndShowsPagedNewScanForExcludingWithoutTruncating()
    {
        using var f = new Fixture(); var scan = Scan("original.exe");
        using var manager = new SteamToolManager(f.Store, (_, _) => Task.FromResult(scan));
        await manager.SaveAsync(new(0, true, []), default); var group = Assert.Single(manager.ReadGroups());
        scan = Scan(Enumerable.Range(0, 129).Select(i => $"game{i}.exe").ToArray());
        var first = await manager.ScanAsync(true, true, default); Assert.Equal(129, first.DetectedCount); Assert.Contains("128", first.Error);
        AssertSameGroup(group, Assert.Single(manager.ReadGroups())); Assert.Equal(64, first.Processes.Length);
        var next = manager.Read(64, first.ScanId); Assert.Equal(64, next.Processes.Length); Assert.Single(manager.Read(128, first.ScanId).Processes);
        await manager.SaveAsync(first.Settings with { ExcludedProcesses = ["game0.exe"] }, default);
        Assert.Equal(128, Assert.Single(manager.ReadGroups()).Entries.Length);
        Assert.Throws<InvalidOperationException>(() => manager.Read(64, first.ScanId));
    }
    [Fact]
    public async Task GroupQuotaFailurePreservesAtomicGroupAndExposesScanWithoutClaimingSuccess()
    {
        using var f = new Fixture();
        for (var i = 0; i < 32; i++) f.Store.Save(new(Guid.NewGuid().ToString("D"), 0, "组" + i, []));
        using var manager = new SteamToolManager(f.Store, (_, _) => Task.FromResult(Scan("one.exe")));
        var result = await manager.SaveAsync(new(0, true, []), default);
        Assert.NotNull(result.Error); Assert.Null(result.LastSuccessUtc); Assert.Equal(1, result.DetectedCount); Assert.Equal(32, manager.ReadGroups().Length); Assert.Null(result.GroupId);
    }
    [Fact]
    public void ToolProtocolRejectsCrossOperationFieldsAndInvalidExclusions()
    {
        var now = DateTimeOffset.UtcNow; var read = new AutomationMessage(1, "read-steam-tool", "test", DeadlineUtc: now.AddSeconds(10));
        AutomationProtocol.ValidateRequest(read, now);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { SteamSettings = new(0, true, []) }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { Type = "save-steam-settings" }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { SteamOffset = 64 }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { Type = "refresh-steam-tool", SteamOffset = 64, SteamScanId = Guid.NewGuid().ToString("N") }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { SteamSettings = new(0, true, []), Type = "apply-shortcuts", Bindings = [] }, now, true));
        Assert.Throws<ArgumentException>(() => SteamTool.Normalize(new(0, true, [@"C:\Apps\one.exe"])));
    }
    private static void AssertSameGroup(ProcessGroup expected, ProcessGroup actual)
    {
        Assert.Equal((expected.Id, expected.Version, expected.Name, expected.AutoManaged), (actual.Id, actual.Version, actual.Name, actual.AutoManaged));
        Assert.Equal(expected.Entries, actual.Entries);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        public SqliteProcessGroupStore Store { get; }
        public Fixture() => Store = new(path);
        public void Dispose() { Store.Dispose(); File.Delete(path); }
    }
}
