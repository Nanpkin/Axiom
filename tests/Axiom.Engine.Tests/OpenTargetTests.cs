using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class OpenTargetTests
{
    private static TaskInvocation Invocation(string kind, string target) => new(OpenTargetCapabilities.Open, 1,
        ImmutableDictionary<string,string>.Empty.Add("kind", kind).Add("target", target).Add("delaySeconds", "2"));
    [Theory]
    [InlineData("https://example.invalid/help")]
    [InlineData("http://example.invalid/")]
    public void WebRequestsCompileAsOneShotCommandsAndCanFollowNetworkRecovery(string target)
    {
        var action = new OpenTargetTaskAction(); var invocation = Invocation("url", target);
        var compiled = action.Compile(invocation, ResourceScope.Session("owner", 1)); Assert.Equal(RuleActionKind.Command, compiled.Kind);
        Assert.Equal(TimeSpan.FromSeconds(2), action.Timing(invocation).MinimumDuration);
        Assert.Contains(new OpenTargetPlugin().Combinations, c => c.DetectionId == SystemEventCapabilities.Event);
    }
    [Fact]
    public void DirectoryValidationIsPureAndSupportsLocalAbsolutePaths()
    {
        var path = Path.Combine(Path.GetTempPath(), "SyntheticAxiomDirectory");
        var validated = OpenTargetCapabilities.Validate(Invocation("folder", path)); Assert.Equal(path, validated.Target);
        Assert.NotNull(new OpenTargetTaskAction().Compile(Invocation("folder", path), ResourceScope.Session("owner", 1)));
    }
    [Theory]
    [InlineData("url", "file:///C:/Windows/cmd.exe")]
    [InlineData("url", "javascript:alert(1)")]
    [InlineData("url", "https://user:password@example.invalid/")]
    [InlineData("url", "ms-settings:privacy")]
    [InlineData("url", "https://example.invalid/\r\nrun")]
    [InlineData("folder", "relative")]
    [InlineData("folder", "\\\\server\\share")]
    public void ScriptProtocolsCredentialsAndAmbiguousPathsAreRejected(string kind, string target) =>
        Assert.Throws<ArgumentException>(() => OpenTargetCapabilities.Validate(Invocation(kind, target)));
}
