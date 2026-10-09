using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class ForegroundTests
{
    [Fact]
    public void MultipleForegroundApplicationsCompileAndDoNotOfferUnaffiliatedKeyboardInput()
    {
        var compiler = new ForegroundTaskDetection(); var invocation = new TaskInvocation(ForegroundCapabilities.Active, 1,
            ImmutableDictionary<string,string>.Empty.Add("processNames", "ExampleEditor.exe\nExampleBrowser.exe"));
        var condition = compiler.Compile(invocation, ResourceScope.Session("owner", 1)); Assert.Equal(2, condition.Dependencies.Count);
        Assert.All(condition.Dependencies, key => Assert.Equal(ForegroundCapabilities.Active, key.CapabilityId));
        Assert.Contains(new ForegroundPlugin().Combinations, c => c.ActionId == WindowCapabilities.State);
        Assert.DoesNotContain(new ForegroundPlugin().Combinations, c => c.ActionId == HotkeyCapabilities.Send);
    }
    [Fact]
    public void SwitchingWithinASelectedSetNeverCreatesAFalseEdgeAndUnknownIsNotFalse()
    {
        var resources = new[] { new ProviderResource(ForegroundCapabilities.Active, "A.exe"), new ProviderResource(ForegroundCapabilities.Active, "B.exe") };
        var values = new Dictionary<string,bool> { ["A.exe"] = true, ["B.exe"] = false };
        foreach (var sample in ForegroundCapabilities.Samples(resources, "B.exe"))
        {
            values[sample.Resource.ResourceId] = sample.Boolean!.Value;
            Assert.Contains(true, values.Values);
        }
        Assert.All(ForegroundCapabilities.Samples(resources, null), s => { Assert.Equal(StateQuality.Unknown, s.Quality); Assert.Null(s.Boolean); });
        Assert.All(ForegroundCapabilities.Samples(resources, "Other.exe"), s => Assert.False(s.Boolean));
    }
    [Theory]
    [InlineData("")]
    [InlineData("C:\\Apps\\Editor.exe")]
    [InlineData("*.exe")]
    public void InvalidForegroundObjectNamesAreRejected(string names) => Assert.Throws<ArgumentException>(() => new ForegroundTaskDetection().Compile(
        new(ForegroundCapabilities.Active, 1, ImmutableDictionary<string,string>.Empty.Add("processNames", names)), ResourceScope.Session("owner", 1)));
}
