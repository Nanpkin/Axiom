using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.Engine.Tests;

public sealed class MinecraftToolTests
{
    [Fact]
    public void SavedLauncherCompilesAgainstTheProductionPlugin()
    {
        var task = MinecraftTool.Configure("Renamed Launcher.exe", false, 0);
        var plugin = Assert.Single(BuiltInAutomationPlugins.Create().OfType<MinecraftPlugin>());
        var compiler = new AutomationTaskCompiler();
        compiler.Register(plugin.Detections.Single());
        compiler.Register(plugin.Actions.Single());
        compiler.RegisterCombination(plugin.Combinations.Single());
        Assert.NotNull(compiler.Compile(task, "owner", ResourceScope.Session("owner", 1)));
        Assert.Empty(task.Action.Parameters);
        Assert.Equal("Renamed Launcher.exe", task.Detection.Parameters["launcher"]);
        Assert.True(MinecraftTool.Configure("PCL2.exe", true, 2).Paused);
    }

    [Theory]
    [InlineData("*.exe")]
    [InlineData("launcher")]
    public void InvalidLauncherIsRejected(string launcher) =>
        Assert.Throws<ArgumentException>(() => MinecraftTool.Configure(launcher, false, 0));

    [Fact]
    public void OnlyJavaWindowRuntimeCanBeSelected()
    {
        var path = Path.Combine(Path.GetTempPath(), "SyntheticJava", "javaw.exe");
        Assert.Equal(path, MinecraftTool.Configure("PCL2.exe", false, 0, path).Action.Parameters["javaPath"]);
        Assert.Throws<ArgumentException>(() => MinecraftTool.Configure("PCL2.exe", false, 0, Path.ChangeExtension(path, ".cmd")));
        Assert.Throws<ArgumentException>(() => MinecraftTool.Configure("PCL2.exe", false, 0, Path.Combine(Path.GetTempPath(), "java.exe")));
    }
}
