using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class RuleCollectionsTests
{
    [Fact]
    public void RenameAndDeleteKeepTaskMembershipAndUnfileWithoutChangingTasks()
    {
        var original = RuleCollections.Empty.Rename(null, "工作");
        var id = original.Selected;
        var filed = original.Move("task-a", id).Move("task-b", id);
        var renamed = filed.Rename(id, "办公");
        Assert.Equal(id, renamed.Selected);
        Assert.Equal("办公", Assert.Single(renamed.Collections).Name);
        Assert.True(renamed.Includes("task-a"));
        Assert.False(renamed.Includes("task-c"));
        Assert.Empty(original.Assignments);
        var removed = renamed.Remove(id);
        Assert.Empty(removed.Collections);
        Assert.Empty(removed.Assignments);
        Assert.Equal("", removed.Selected);
        Assert.Equal(RuleCollections.Unfiled, removed.CollectionOf("task-a"));
        Assert.True(removed.Includes("task-a"));
        Assert.Equal(RuleCollections.Unfiled, filed.Move("task-a", RuleCollections.Unfiled).CollectionOf("task-a"));
        Assert.Equal(id, filed.CollectionOf("task-a"));
    }

    [Fact]
    public void NamesAndMissingCollectionsAreRejectedWithoutMutatingExistingState()
    {
        var original = RuleCollections.Empty.Rename(null, "Work");
        Assert.Throws<ArgumentException>(() => original.Rename(null, " work "));
        Assert.Throws<ArgumentException>(() => original.Rename(null, "未分类"));
        Assert.Throws<ArgumentException>(() => original.Rename(null, " "));
        Assert.Throws<ArgumentException>(() => original.Rename(null, new string('a', 25)));
        Assert.Throws<ArgumentException>(() => original.Move("task", "missing"));
        Assert.Single(original.Collections);
        Assert.Empty(original.Assignments);
    }

    [Fact]
    public void ReloadRestoresSelectionAndMembershipAndFailedReplacementPreservesState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "axiom-collections-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "collections.json");
        try
        {
            var original = RuleCollections.Empty.Rename(null, "日常");
            original = original.Move("task-a", original.Selected);
            original.Save(path);
            var loaded = RuleCollections.Load(path);
            Assert.Equal(original.Selected, loaded.Selected);
            Assert.Equal(original.Selected, loaded.CollectionOf("task-a"));
            Assert.False(loaded.Includes("task-b"));
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failure = Record.Exception(() => loaded.Rename(loaded.Selected, "新名称").Save(path));
                Assert.True(failure is IOException or UnauthorizedAccessException, "The locked destination must reject replacement.");
            }
            Assert.Equal("日常", Assert.Single(RuleCollections.Load(path).Collections).Name);
            Assert.Single(Directory.GetFiles(directory));
            File.WriteAllText(path, "not json");
            Assert.Throws<System.Text.Json.JsonException>(() => RuleCollections.Load(path));
            Assert.Equal("not json", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
