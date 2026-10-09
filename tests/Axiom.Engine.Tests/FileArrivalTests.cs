using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class FileArrivalTests
{
    [Fact]
    public void FileArrivalCompilesCanonicalLocalDirectoryAndRejectsNetworkOrUnknownFilters()
    {
        var folder = Path.Combine(Path.GetTempPath(), "synthetic-watch");
        var invocation = new TaskInvocation(FileArrivalCapabilities.Arrived, 1,
            new Dictionary<string, string> { ["directory"] = folder, ["kind"] = "pdf" }.ToImmutableDictionary());
        var config = FileArrivalCapabilities.Validate(invocation); var resource = FileArrivalCapabilities.Resource(config);
        Assert.Equal(config, FileArrivalCapabilities.ParseResource(resource));
        var condition = new FileArrivalTaskDetection().Compile(invocation, ResourceScope.Session("owner", 1));
        Assert.Equal(ConditionKind.Event, condition.Kind); Assert.Equal(resource, Assert.Single(condition.Dependencies).ResourceId);
        foreach (var directory in new[] { "relative", "\\\\synthetic-server\\share", "//synthetic-server/share", folder + "\n", folder + "*" })
            Assert.Throws<ArgumentException>(() => FileArrivalCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem("directory", directory) }));
        Assert.Throws<ArgumentException>(() => FileArrivalCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem("kind", "*.exe") }));
        Assert.Throws<ArgumentException>(() => FileArrivalCapabilities.ParseResource("pdf:" + folder + Path.DirectorySeparatorChar));
    }
    [Theory]
    [InlineData("report.PDF", "pdf", true)]
    [InlineData("report.pdf.part", "pdf", false)]
    [InlineData("report.docx", "office", true)]
    [InlineData("picture.WEBP", "image", true)]
    [InlineData("bundle.zip", "archive", true)]
    [InlineData("opaque", "any", true)]
    public void FilenameCategoriesNeedNoContentReads(string name, string kind, bool expected) => Assert.Equal(expected, FileArrivalSource.Matches(name, kind));

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealDirectoryNotificationStartsWithoutReplayAndSharesMatchingFiltersThenReleases(bool rename)
    {
        var folder = Path.Combine(Path.GetTempPath(), "axiom-watch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "existing.pdf"), "synthetic");
        await File.WriteAllTextAsync(Path.Combine(folder, "staged.partial"), "synthetic");
        var time = new ManualTimeProvider(); var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        time.TimerCreated = _ => deadline.TrySetResult();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var events = Channel.CreateBounded<string[]>(4);
        using var lifetime = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var run = FileArrivalSource.RunAsync([new("pdf", folder, "pdf"), new("all", folder, "any")], ready.SetResult,
            resources => Assert.True(events.Writer.TryWrite(resources)), lifetime.Token, time);
        try
        {
            await ready.Task.WaitAsync(timeout.Token); Assert.False(deadline.Task.IsCompleted); Assert.False(events.Reader.TryRead(out _));
            if (rename) File.Move(Path.Combine(folder, "staged.partial"), Path.Combine(folder, "arrived.PDF"));
            else await File.WriteAllTextAsync(Path.Combine(folder, "arrived.PDF"), "synthetic");
            await deadline.Task.WaitAsync(timeout.Token); Assert.False(events.Reader.TryRead(out _));
            time.Advance(TimeSpan.FromSeconds(1)); var batch = await events.Reader.ReadAsync(timeout.Token);
            Assert.Equal(new[] { "all", "pdf" }, batch.Order());
        }
        finally { lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Directory.Delete(folder, true); }
        Assert.All(time.Timers, timer => Assert.Null(timer.At));
    }
    [Fact(Timeout = 10000)]
    public async Task TooManySubscriptionsStayUnknownWithoutEndingOtherProviderStreams()
    {
        var provider = new FileArrivalCapabilityProvider();
        var resources = Enumerable.Range(0, 17).Select(i => new ProviderResource(FileArrivalCapabilities.Arrived,
            FileArrivalCapabilities.Resource((Path.Combine(Path.GetTempPath(), "synthetic-watch-" + i), "pdf")))).ToArray();
        var updates = Channel.CreateBounded<ProviderUpdate>(2); using var lifetime = new CancellationTokenSource();
        var run = provider.ObserveAsync(resources, update => Assert.True(updates.Writer.TryWrite(update)), lifetime.Token);
        var snapshot = await updates.Reader.ReadAsync(); Assert.True(snapshot.Snapshot); Assert.Equal(17, snapshot.Samples.Length);
        Assert.All(snapshot.Samples, sample => { Assert.Equal(StateQuality.Unknown, sample.Quality); Assert.Contains("16", sample.Reason); });
        Assert.False(run.IsCompleted); lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
