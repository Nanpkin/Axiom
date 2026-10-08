namespace Axiom.Contracts;

public sealed record AxiomInstallLayout(string Root, string Host, string Agent, string Ui)
{
    public string Launcher => Path.Combine(Root, "Axiom.exe");
    public bool Complete => new[] { Launcher, Host, Agent, Ui }.All(File.Exists);
    public static AxiomInstallLayout At(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(Path.Combine(root, "app")))
            return new(root, Path.Combine(root, "app", "Axiom.ServiceHost.exe"), Path.Combine(root, "app", "Axiom.SessionAgent.exe"),
                Path.Combine(root, "app", "Axiom.WinUI.exe"));
        return new(root, Path.Combine(root, "backend", "host", "Axiom.ServiceHost.exe"),
            Path.Combine(root, "backend", "agent", "Axiom.SessionAgent.exe"), Path.Combine(root, "ui", "Axiom.WinUI.exe"));
    }
    public static AxiomInstallLayout ForComponent(string directory)
    {
        var component = new DirectoryInfo(directory);
        return At(component.Name.Equals("app", StringComparison.OrdinalIgnoreCase)
            ? component.Parent!.FullName : Path.GetFullPath(Path.Combine(directory, "..", "..")));
    }
}
