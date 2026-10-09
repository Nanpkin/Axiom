using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Axiom.Domain;

namespace Axiom.Engine.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void AllTimeoutFactsAndTheoriesUseAnAsyncSignatureSupportedByTheCurrentXunitRunner()
    {
        var timedMethods = typeof(ArchitectureTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<FactAttribute>() is { Timeout: > 0 }).ToArray();
        Assert.NotEmpty(timedMethods);
        // TheoryAttribute inherits FactAttribute. Use the same classifier as the pinned runner's timeout path.
        var violations = timedMethods.Where(method => !Xunit.Sdk.AsyncUtility.IsAsync(method))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}: Timeout requires an xUnit-supported async signature").ToArray();
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    // Explicit assembly allowlist: adding a dependency requires a conscious boundary review.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "System.Runtime", "System.Collections", "System.Collections.Immutable", "System.Linq",
        "System.Threading", "System.Threading.Tasks", "System.Memory", "System.Runtime.Extensions",
        "System.ObjectModel", "System.Threading.Thread", "Axiom.Domain"
    };

    [Fact]
    public void ProductionProjectsHaveOnlyTheApprovedReferenceGraph()
    {
        var root = SmokeTests.FindRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        Assert.Equal("net10.0", props.Descendants("TargetFramework").Single().Value);
        foreach (var name in new[] { "Domain", "Engine", "Infrastructure" })
        {
            var project = XDocument.Load(Path.Combine(root, "src", $"Axiom.{name}", $"Axiom.{name}.csproj"));
            var packages = project.Descendants("PackageReference")
                .Select(x => (Name: x.Attribute("Include")?.Value, Version: x.Attribute("Version")?.Value)).ToArray();
            if (name == "Infrastructure") Assert.Equal([("Microsoft.Data.Sqlite", "10.0.12")], packages);
            else Assert.Empty(packages);
            Assert.Empty(project.Descendants("Reference"));
            Assert.Empty(project.Descendants("COMReference"));
            Assert.Empty(project.Descendants("TargetFramework"));
            var refs = project.Descendants("ProjectReference").Select(x => x.Attribute("Include")!.Value).ToArray();
            Assert.Equal(name switch
            {
                "Domain" => [],
                "Engine" => ["../Axiom.Domain/Axiom.Domain.csproj"],
                _ => new[] { "../Axiom.Contracts/Axiom.Contracts.csproj", "../Axiom.Engine/Axiom.Engine.csproj" }
            }, refs);
        }
    }

    [Fact]
    public void VisibleClientOnlyReferencesProtocolAndServiceHostIsCompositionRoot()
    {
        var root = SmokeTests.FindRoot();
        string[] References(string name) => XDocument.Load(Path.Combine(root, "src", name, name + ".csproj"))
            .Descendants("ProjectReference").Select(x => x.Attribute("Include")!.Value).ToArray();
        Assert.Equal(["../Axiom.Contracts/Axiom.Contracts.csproj"], References("Axiom.WinUI"));
        Assert.Equal(new[]
        {
            "../Axiom.Contracts/Axiom.Contracts.csproj", "../Axiom.Engine/Axiom.Engine.csproj",
            "../Axiom.Infrastructure/Axiom.Infrastructure.csproj"
        }, References("Axiom.ServiceHost"));
        Assert.Equal(["../Axiom.Contracts/Axiom.Contracts.csproj", "../Axiom.Providers.Windows/Axiom.Providers.Windows.csproj"], References("Axiom.SessionAgent"));
        Assert.Empty(References("Axiom.Providers.Windows"));
        Assert.Equal(["../Axiom.Domain/Axiom.Domain.csproj"], References("Axiom.Contracts"));
    }

    [Fact]
    public void CompiledCoreContainsNoPlatformDependencyPInvokeOrHiddenClock()
    {
        var violations = new[] { typeof(ResourceKey).Assembly, typeof(EngineHost).Assembly }
            .SelectMany(Inspect).ToArray();
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> Inspect(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.AssemblyReferences)
        {
            var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
            if (!Allowed.Contains(name)) yield return $"{assembly.GetName().Name}: forbidden assembly {name}";
        }
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                yield return $"{assembly.GetName().Name}: P/Invoke {reader.GetString(method.Name)}";
        }
        foreach (var handle in reader.TypeReferences)
        {
            var type = reader.GetTypeReference(handle);
            var name = reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
            if (ForbiddenType(name)) yield return $"{assembly.GetName().Name}: forbidden type {name}";
        }
        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            var typeName = reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
            var memberName = reader.GetString(member.Name);
            if (ForbiddenMember(typeName, memberName)) yield return $"{assembly.GetName().Name}: forbidden API {typeName}.{memberName}";
        }
    }

    internal static bool ForbiddenType(string name) =>
        name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) ||
        name.StartsWith("System.Windows.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.UI.", StringComparison.Ordinal) ||
        name.StartsWith("Windows.", StringComparison.Ordinal) ||
        name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".Providers.", StringComparison.Ordinal) ||
        name.StartsWith("System.Net.", StringComparison.Ordinal) ||
        name is "System.Runtime.InteropServices.NativeLibrary" or "System.Threading.Timer" or
        "System.Threading.PeriodicTimer" or "System.Timers.Timer" or "System.Diagnostics.Process" or
        "System.IO.File" or "System.IO.Directory";

    internal static bool ForbiddenMember(string type, string member) =>
        type is "System.DateTime" or "System.DateTimeOffset" && member is "get_Now" or "get_UtcNow" ||
        type == "System.Environment" && member is "get_TickCount" or "get_TickCount64" ||
        type == "System.Diagnostics.Stopwatch" && member is "StartNew" or "GetTimestamp" ||
        type == "System.Threading.Tasks.Task" && member is "Run" or "Delay" ||
        type == "System.Threading.Thread" && member == "Sleep" ||
        type == "System.Random" ||
        type == "System.TimeProvider" && member == "get_System";

    [Theory]
    [InlineData("Microsoft.Win32.Registry")]
    [InlineData("Microsoft.Data.Sqlite.SqliteConnection")]
    [InlineData("System.Runtime.InteropServices.NativeLibrary")]
    [InlineData("System.Threading.PeriodicTimer")]
    [InlineData("Axiom.Providers.Windows.ProcessProvider")]
    public void BoundaryClassifierRejectsKnownViolations(string type) => Assert.True(ForbiddenType(type));
}
