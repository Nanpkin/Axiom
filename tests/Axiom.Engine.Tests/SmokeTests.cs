namespace Axiom.Engine.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void EngineReferencesDomainAndNoAdapter()
    {
        var project = Path.Combine(FindRoot(), "src", "Axiom.Engine", "Axiom.Engine.csproj");
        var references = System.Xml.Linq.XDocument.Load(project).Descendants("ProjectReference")
            .Select(x => (string?)x.Attribute("Include") ?? "").ToArray();
        Assert.Equal(["../Axiom.Domain/Axiom.Domain.csproj"], references);
    }

    internal static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Axiom.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root missing");
    }
}
