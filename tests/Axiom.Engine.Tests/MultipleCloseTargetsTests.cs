using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class MultipleCloseTargetsTests
{
    private static TaskInvocation Action(bool windows, string paths) => new(windows ? WindowCapabilities.Close : "program.close", 1,
        (windows ? WindowCapabilities.Descriptor : ProgramCapabilities.Describe(false)).Fields.Where(f => f.DefaultValue is not null)
            .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("executable", paths));
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OldAndMultipleTargetsCompileAndPersistWithoutLosingFields(bool windows)
    {
        ITaskActionCompiler compiler = windows ? new CloseWindowTaskAction() : new ProgramTaskAction(false);
        var scope = ResourceScope.Session("owner", 1);
        var old = Action(windows, @"D:\Apps\One.exe");
        Assert.Equal(@"d:\apps\one.exe", compiler.Compile(old, scope).Key.ResourceId);
        Assert.Equal(TaskFieldKind.ExecutableSelection, compiler.Descriptor.Fields.Single(f => f.Id == "executable").Kind);
        var multiple = Action(windows, "D:\\Apps\\One.exe\r\n C:\\Apps\\Two.exe \nD:\\APPS\\ONE.EXE");
        var key = compiler.Compile(multiple, scope).Key;
        Assert.Equal(key, compiler.Compile(Action(windows, "C:\\Apps\\Two.exe\nD:\\Apps\\One.exe"), scope).Key);
        Assert.NotEqual(key, compiler.Compile(old, scope).Key);
        var task = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "多个关闭目标", false,
            new(AxiomStartupPlugin.CapabilityId, 1, ImmutableDictionary<string,string>.Empty.Add("frequency", "firstBoot")), multiple);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) task = await store.SaveAsync(task, default);
            using (var store = new SqliteAutomationTaskStore(path))
            {
                var saved = Assert.Single(await store.ListAsync()); Assert.Equal(task.Id, saved.Id);
                Assert.Equal(multiple.Parameters["executable"], saved.Action.Parameters["executable"]);
                Assert.Equal("firstBoot", saved.Detection.Parameters["frequency"]);
            }
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void PathSelectionIsAtomicDeduplicatesAndPreservesFullPathsAndBounds()
    {
        var state = new ProcessSelectionState(@"D:\Apps\One.exe", true);
        state.Set([@"d:\APPS\one.exe", @"C:\Apps\Two.exe"], true); Assert.Equal(2, state.Names.Count);
        Assert.Throws<ArgumentException>(() => state.Set([@"C:\Apps\Good.exe", "bad.exe"], true)); Assert.Equal(2, state.Names.Count);
        state.Set([@"d:\apps\one.exe"], false); Assert.Equal(@"C:\Apps\Two.exe", state.Value);
        foreach (var bad in new[] { @"\\server\apps\one.exe", @"C:\Windows\cmd.exe", @"C:\Apps\Axiom.exe", @"C:\Apps\tool.exe --arg", "relative.exe" })
            Assert.Throws<ArgumentException>(() => ExecutablePaths.Parse(@"C:\Apps\Good.exe" + "\n" + bad));
        state.Set(Enumerable.Range(1, 31).Select(i => $@"C:\Apps\Target{i}.exe"), true);
        Assert.Equal(32, state.Names.Count);
        Assert.Throws<ArgumentException>(() => state.Set([@"C:\Apps\Extra.exe"], true)); Assert.Equal(32, state.Names.Count);
        Assert.Throws<ArgumentException>(() => ExecutablePaths.Parse(new string('a', 32769)));
        Assert.Throws<ArgumentException>(() => ProgramCapabilities.ValidateTargets(new("program.start", 1,
            ImmutableDictionary<string,string>.Empty.Add("executable", "C:\\Apps\\One.exe\nC:\\Apps\\Two.exe"))));
    }
    [Fact]
    public async Task BatchStartsAllWaitsTogetherAndRecordsPartialFailureWithoutSkippingTargets()
    {
        var started = new HashSet<string>(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = WindowsProgramBatch.ExecuteAsync(["one", "two", "three"], async (path, token) =>
        {
            started.Add(path); await release.Task.WaitAsync(token);
            if (path == "two") throw new UnauthorizedAccessException("权限不足");
            return new(true, "完成");
        }, default);
        Assert.Equal(3, started.Count); Assert.False(pending.IsCompleted); release.SetResult();
        var result = await pending; Assert.False(result.Success); Assert.Contains("2/3", result.Reason);
        foreach (var path in started) Assert.Contains(path, result.Reason); Assert.Contains("权限不足", result.Reason);
    }
    [Fact]
    public async Task BatchCancellationCancelsEveryPendingTargetAndDoesNotReportSuccess()
    {
        using var cancel = new CancellationTokenSource(); var finished = 0;
        var pending = WindowsProgramBatch.ExecuteAsync(["one", "two"], async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(true, "关闭"); }
            finally { Interlocked.Increment(ref finished); }
        }, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(2, finished);
    }
}
