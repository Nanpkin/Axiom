using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Axiom.Engine.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class CoreBenchmarks
{
    [Params(100, 1000, 10000)]
    public int Rules { get; set; }
    private CoreFixture updates = null!;
    private CoreFixture delivery = null!;

    [GlobalSetup]
    public void Setup()
    {
        updates = new(Rules);
        delivery = new(Rules, subscribe: true);
    }
    [Benchmark]
    public UpdateResult UpdateAndSelect() => updates.Update();
    [Benchmark]
    public int IndexLookup() => updates.Engine.MatchDependencies(CoreFixture.Key).Length;
    [Benchmark]
    public StateDelivery UpdateSelectDeliver() => delivery.Deliver();
    [GlobalCleanup]
    public void Cleanup() { updates.Dispose(); delivery.Dispose(); }
}
