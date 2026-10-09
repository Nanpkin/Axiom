using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class WindowStateTests
{
    [Theory]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("restore")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("center")]
    [InlineData("top-left")]
    [InlineData("top-right")]
    [InlineData("bottom-left")]
    [InlineData("bottom-right")]
    [InlineData("topmost")]
    [InlineData("notopmost")]
    public void StateActionCompilesWithDelayAndMultipleTargets(string state)
    {
        var action = new TaskInvocation(WindowCapabilities.State, 1, ImmutableDictionary<string, string>.Empty
            .Add("executable", Path.Combine(Path.GetTempPath(), "Editor.exe") + "\n" + Path.Combine(Path.GetTempPath(), "Chat.exe")).Add("state", state).Add("waitSeconds", "30").Add("delaySeconds", "2"));
        var compiler = new WindowStateTaskAction();
        Assert.Equal(2, WindowCapabilities.ValidateTargets(action).Length);
        var rule = compiler.Compile(action, ResourceScope.Session("owner", 1));
        Assert.Equal(RuleActionKind.Command, rule.Kind);
        Assert.Equal(WindowCapabilities.State, rule.Key.CapabilityId);
        Assert.Equal(TimeSpan.FromSeconds(2), compiler.Timing(action).MinimumDuration);
        Assert.Contains(new WindowPlugin().Combinations, c => c.DetectionId == HotkeyCapabilities.Pressed && c.ActionId == WindowCapabilities.State);
    }

    [Fact]
    public void LayoutUsesWorkAreaIncludingNegativeMonitorOriginAndOddWidths()
    {
        Assert.Equal((-1920, 40, 960, 1040), Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("left", -1920, 40, 1921, 1040));
        Assert.Equal((-960, 40, 961, 1040), Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("right", -1920, 40, 1921, 1040));
        Assert.Equal((192, 104, 1536, 832), Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("center", 0, 0, 1920, 1040));
        Assert.Throws<ArgumentException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("left", 0, 0, 0, 10));
    }

    [Theory]
    [InlineData(-1920, -1080, 1921, 1081)]
    [InlineData(0, 40, 1920, 1040)]
    [InlineData(-1, -1, 2, 2)]
    public void FourCornersPartitionTheEntireWorkAreaWithoutGapsOrOverlaps(int x, int y, int width, int height)
    {
        var a = Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("top-left", x, y, width, height);
        var b = Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("top-right", x, y, width, height);
        var c = Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("bottom-left", x, y, width, height);
        var d = Axiom.Providers.Windows.WindowLayoutGeometry.Calculate("bottom-right", x, y, width, height);
        Assert.Equal((x, y, width / 2, height / 2), a);
        Assert.Equal((x + width / 2, y, width - width / 2, height / 2), b);
        Assert.Equal((x, y + height / 2, width / 2, height - height / 2), c);
        Assert.Equal((x + width / 2, y + height / 2, width - width / 2, height - height / 2), d);
        Assert.Equal((long)width * height, new[] { a, b, c, d }.Sum(r => (long)r.Width * r.Height));
        Assert.All(new[] { a, b, c, d }, r => { Assert.True(r.Width > 0); Assert.True(r.Height > 0); });
        Assert.Equal(a.X + a.Width, b.X); Assert.Equal(a.Y + a.Height, c.Y);
        Assert.Equal(x + width, d.X + d.Width); Assert.Equal(y + height, d.Y + d.Height);
    }

    [Theory]
    [InlineData("top-left")]
    [InlineData("top-right")]
    [InlineData("bottom-left")]
    [InlineData("bottom-right")]
    public void CornerChoicesAreAdditiveSchemaOneAndRejectTinyOrOverflowingGeometry(string corner)
    {
        Assert.Equal(1, WindowCapabilities.StateDescriptor.SchemaVersion);
        Assert.Contains(WindowCapabilities.StateDescriptor.Fields.Single(f => f.Id == "state").Choices, c => c.Value == corner);
        Assert.Throws<ArgumentException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Calculate(corner, 0, 0, 1, 10));
        Assert.Throws<ArgumentException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Calculate(corner, 0, 0, 10, 1));
        // Existing usage evidence intentionally retains its three approximate layout categories.
        var rect = Axiom.Providers.Windows.WindowLayoutGeometry.Calculate(corner, -1920, -1080, 1921, 1081);
        Assert.Equal("normal", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(rect.X, rect.Y, rect.Width, rect.Height, -1920, -1080, 1921, 1081, 16));
        if (corner.EndsWith("right", StringComparison.Ordinal))
            Assert.Throws<OverflowException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Calculate(corner, int.MaxValue, 0, 10, 10));
        if (corner.StartsWith("bottom", StringComparison.Ordinal))
            Assert.Throws<OverflowException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Calculate(corner, 0, int.MaxValue, 10, 10));
    }
    [Fact]
    public void LayoutCategoriesAllowFrameMarginsButDoNotInventAMatchForArbitraryPlacement()
    {
        Assert.Equal("left", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(-1928, 32, 976, 1056, -1920, 40, 1921, 1040, 16));
        Assert.Equal("right", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(-960, 40, 961, 1040, -1920, 40, 1921, 1040, 16));
        Assert.Equal("center", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(192, 104, 1536, 832, 0, 0, 1920, 1040, 16));
        Assert.Equal("normal", Axiom.Providers.Windows.WindowLayoutGeometry.Classify(100, 100, 500, 300, 0, 0, 1920, 1040, 16));
        Assert.Throws<ArgumentException>(() => Axiom.Providers.Windows.WindowLayoutGeometry.Classify(0, 0, 0, 20, 0, 0, 1920, 1040, 16));
    }
    [Theory]
    [InlineData("state", "unsupported-state")]
    [InlineData("waitSeconds", "31")]
    [InlineData("executable", "cmd.exe")]
    [InlineData("execute", "anything")]
    public void InvalidStateOrExtraParametersAreRejected(string key, string value)
    {
        var action = new TaskInvocation(WindowCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("executable", Path.Combine(Path.GetTempPath(), "Editor.exe")).SetItem(key, value));
        Assert.Throws<ArgumentException>(() => new WindowStateTaskAction().Compile(action, ResourceScope.Session("owner", 1)));
    }
}
