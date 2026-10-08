using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class NotificationTests
{
    private static TaskInvocation Invocation() => new(NotificationCapabilities.Show, 1,
        new Dictionary<string, string> { ["title"] = "提醒", ["body"] = "请休息\n片刻", ["sound"] = "false", ["delaySeconds"] = "0" }.ToImmutableDictionary());
    [Fact]
    public void DifferentMessagesShareOneArbitratedResourceAndAreReceiptOnly()
    {
        var compiler = new NotificationTaskAction(); var scope = ResourceScope.Session("owner", 1);
        var first = compiler.Compile(Invocation(), scope);
        Assert.Equal("desktop", first.Key.ResourceId);
        Assert.Equal(first.Key, compiler.Compile(Invocation() with { Parameters = Invocation().Parameters.SetItem("body", "another") }, scope).Key);
        Assert.False(NotificationCapabilities.Validate(Invocation()).Sound);
        Assert.Contains(new NotificationPlugin().Combinations, c => c.DetectionId == FileArrivalCapabilities.Arrived);
        Assert.Contains(BuiltInAutomationPlugins.Create(), p => p.Id == "axiom.notification");
    }
    [Theory]
    [InlineData("title", "")]
    [InlineData("title", "bad\nline")]
    [InlineData("body", "bad\0text")]
    [InlineData("sound", "yes")]
    [InlineData("delaySeconds", "0.5")]
    [InlineData("delaySeconds", "86401")]
    public void InvalidReminderCannotCompile(string field, string value) => Assert.Throws<ArgumentException>(() =>
        new NotificationTaskAction().Compile(Invocation() with { Parameters = Invocation().Parameters.SetItem(field, value) }, ResourceScope.Session("owner", 1)));
    [Fact]
    public void NativeStringLimitsAreValidatedBeforeDispatch()
    {
        foreach (var (field, length) in new[] { ("title", 64), ("body", 256) })
            Assert.Throws<ArgumentException>(() => NotificationCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem(field, new string('a', length)) }));
    }
}
