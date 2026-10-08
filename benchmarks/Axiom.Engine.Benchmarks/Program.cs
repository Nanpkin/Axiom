using BenchmarkDotNet.Running;

namespace Axiom.Engine.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "harness") await Harness.Run(args.Skip(1).ToArray());
        else BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
