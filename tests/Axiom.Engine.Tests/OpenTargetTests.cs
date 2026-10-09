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
    [InlineData(".pdf")]
    [InlineData(".DOCX")]
    [InlineData(".txt")]
    [InlineData(".png")]
    public void DocumentCompilationOnlyChecksConfiguredPathAndNeverRequiresOrReadsTheFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "SyntheticMissing-" + Guid.NewGuid().ToString("N") + extension);
        Assert.False(File.Exists(path));
        var invocation = Invocation("document", path); var target = OpenTargetCapabilities.Validate(invocation);
        Assert.Equal(path, target.Target); Assert.Equal("document", target.Kind);
        Assert.Equal(RuleActionKind.Command, new OpenTargetTaskAction().Compile(invocation, ResourceScope.Session("owner", 1)).Kind);
    }
    [Theory]
    [InlineData(".exe")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    [InlineData(".js")]
    [InlineData(".lnk")]
    [InlineData(".url")]
    [InlineData(".docm")]
    [InlineData(".xlsx.exe")]
    [InlineData(".pdf:stream")]
    public void DocumentsRejectExecutableShortcutsMacroFormatsAndAlternateStreams(string suffix) =>
        Assert.Throws<ArgumentException>(() => OpenTargetCapabilities.Validate(Invocation("document", Path.Combine(Path.GetTempPath(), "Synthetic" + suffix))));
    [Fact]
    public void LocalPathCaseSharesResourceWhileWebPathCaseRemainsDistinct()
    {
        var compiler = new OpenTargetTaskAction(); var scope = ResourceScope.Session("owner", 1);
        var path = Path.Combine(Path.GetTempPath(), "SyntheticPlan.pdf");
        Assert.Equal(compiler.Compile(Invocation("document", path), scope).Key, compiler.Compile(Invocation("document", path.ToUpperInvariant()), scope).Key);
        Assert.NotEqual(compiler.Compile(Invocation("url", "https://example.invalid/Plan"), scope).Key, compiler.Compile(Invocation("url", "https://example.invalid/plan"), scope).Key);
    }
    [Fact]
    public void ForwardSlashNetworkSharesCannotBypassLocalOnlyPathValidation()
    {
        foreach (var kind in new[] { "folder", "document" })
            Assert.Throws<ArgumentException>(() => OpenTargetCapabilities.Validate(Invocation(kind, "//synthetic-server/share/file.pdf")));
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
