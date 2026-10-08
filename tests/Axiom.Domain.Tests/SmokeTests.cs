namespace Axiom.Domain.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void CoreTargetsNet10WithoutWindowsTarget()
    {
        var framework = typeof(SmokeTests).Assembly.GetCustomAttributes(false)
            .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>().Single();
        Assert.Equal(".NETCoreApp,Version=v10.0", framework.FrameworkName);
    }
}
