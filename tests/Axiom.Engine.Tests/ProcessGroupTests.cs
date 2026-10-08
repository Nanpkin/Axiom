using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class ProcessGroupTests
{
    [Fact]
    public void EditingExistingHdrTaskPreservesBothProcessesWithoutDirtyingOrChangingIdentity()
    {
        var task = GameHdrTasks.FromLegacy(new(Guid.NewGuid().ToString("D"), 3, "HDR 游戏", "yuanshen.exe", "screen", true, false, "yuanshen.exe\nre4.exe"));
        var editor = new TaskEditorState(); editor.Load(task);
        var picker = new ProcessSelectionState(editor.Detection!.Parameters["processNames"]);
        Assert.Equal(new[] { "yuanshen.exe", "re4.exe" }, picker.Names);
        Assert.False(editor.Dirty); Assert.Equal(task, editor.Build());
        picker.Set(["cs2.exe"], true); editor.SetParameter(TaskCapabilityKind.Detection, "processNames", picker.Value);
        Assert.True(editor.Dirty);
        Assert.Equal("yuanshen.exe\nre4.exe\ncs2.exe", GameHdrTasks.ToLegacy(editor.Build()).ProcessNamesText);
    }
    [Fact]
    public void GroupSelectionDeduplicatesAndDeselectingOneGroupKeepsOtherSelections()
    {
        var picker = new ProcessSelectionState("yuanshen.exe\nre4.exe");
        picker.Set(["RE4.EXE", "cs2.exe"], true);
        Assert.Equal(3, picker.Names.Count);
        picker.Set(["RE4.EXE", "cs2.exe"], false);
        Assert.Equal(new[] { "yuanshen.exe" }, picker.Names);
        Assert.Throws<ArgumentException>(() => picker.Set(["good.exe", "../bad.exe"], true));
        Assert.Equal(new[] { "yuanshen.exe" }, picker.Names);
    }
    [Fact]
    public void Full128ProcessGroupCanBeSelectedAndCompiledWithinExistingSharedWatchLimit()
    {
        var names = Enumerable.Range(1, 128).Select(x => $"game{x}.exe").ToArray();
        var picker = new ProcessSelectionState(null); picker.Set(names, true);
        Assert.Equal(128, ProcessNameSelection.Parse(picker.Value).Length);
        Assert.Throws<ArgumentException>(() => picker.Set(["another.exe"], true));
        Assert.Equal(128, picker.Names.Count);
        var task = GameHdrTasks.FromLegacy(new(Guid.NewGuid().ToString("D"), 0, "group", names[0], "screen", true, false, picker.Value));
        Assert.Equal(128, GameHdrTasks.CreateCompiler().Compile(task, "user", ResourceScope.Session("user", 1)).Condition.Dependencies.Count);
    }
    [Fact]
    public void GroupsPersistAcrossRestartAndStaleEditorsCannotOverwriteOrDeleteThem()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var group = new ProcessGroup(Guid.NewGuid().ToString("D"), 0, "HDR 游戏", [new("yuanshen.exe", "原神"), new("RE4.EXE", "RE4"), new("re4.exe", "重复")]);
        try
        {
            using (var store = new SqliteProcessGroupStore(path))
            {
                group = store.Save(group); Assert.Equal(1, group.Version); Assert.Equal(2, group.Entries.Length);
                Assert.Throws<InvalidOperationException>(() => store.Save(group with { Version = 0, Name = "old" }));
                Assert.Throws<InvalidOperationException>(() => store.Delete(group.Id, 2));
                Assert.Throws<ArgumentException>(() => store.Save(group with { Id = Guid.NewGuid().ToString("D"), Version = 0 }));
            }
            using (var store = new SqliteProcessGroupStore(path))
            {
                var restored = Assert.Single(store.Read()); Assert.Equal(group.Id, restored.Id); Assert.Equal(group.Entries, restored.Entries);
                group = store.Save(restored with { Name = "游戏" }); Assert.Equal(2, group.Version);
                store.Delete(group.Id, group.Version); Assert.Empty(store.Read());
            }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void GroupAndSteamIpcRejectCrossOperationFieldsAndWrongPageTokens()
    {
        var now = DateTimeOffset.UtcNow;
        var group = new ProcessGroup(Guid.NewGuid().ToString("D"), 0, "test", []);
        var read = new AutomationMessage(1, "read", "request", DeadlineUtc: now.AddSeconds(10));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { Group = group }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { RefreshSteam = true }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { Type = "scan-steam", SteamOffset = 40 }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(read with { Type = "scan-steam", SteamOffset = 40, SteamScanId = Guid.NewGuid().ToString("N"), RefreshSteam = true }, now));
        AutomationProtocol.ValidateRequest(read with { Type = "save-group", Group = group }, now);
        AutomationProtocol.ValidateRequest(read with { Type = "scan-steam", RefreshSteam = true }, now);
    }
}
