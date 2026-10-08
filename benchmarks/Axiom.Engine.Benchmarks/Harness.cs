using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Axiom.Engine.Benchmarks;

internal static class Harness
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static double Percentile(IEnumerable<double> data, double quantile)
    {
        var sorted = data.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * quantile) - 1, 0, sorted.Length - 1)];
    }

    internal static async Task Run(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "matrix";
        var output = args.Length > 1 ? args[1] : "artifacts/performance/bootstrap";
        Directory.CreateDirectory(output);
        var seconds = args.Length > 2 ? int.Parse(args[2]) : mode == "idle" ? 15 : 3;
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var report = mode switch
        {
            "idle" => await Idle(seconds),
            "matrix" => await Matrix(seconds),
            "setup" => Setup(),
            _ => throw new ArgumentException("Use harness idle|matrix|setup [output] [seconds].")
        };
        var envelope = new
        {
            Scope = "Engine-only: no rule evaluation, Intent, Provider, modes or hosts",
            CompletedUtc = DateTimeOffset.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount, DebuggerAttached = Debugger.IsAttached,
            Configuration = "Release required", Result = report
        };
        var path = Path.Combine(output, mode + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(envelope, Json));
        Console.WriteLine(path);
        Console.WriteLine(JsonSerializer.Serialize(envelope, Json));
    }

    private static object Setup()
    {
        var rows = new List<object>();
        foreach (var rules in new[] { 100, 1000, 10000 })
        {
            using (var warmup = new CoreFixture(rules)) { }
            for (var trial = 1; trial <= 3; trial++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                using var f = new CoreFixture(rules);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                var candidates = f.Engine.MatchDependencies(CoreFixture.Key).Length;
                if (candidates != 10) throw new InvalidOperationException("Invalid setup index.");
                rows.Add(new { Rules = rules, Trial = trial, ElapsedMs = elapsed, AllocatedBytes = bytes, Candidates = candidates });
            }
        }
        return new { Rows = rows, Limitation = "One-time Engine/source plus full dependency-index construction; three short samples per size, not startup of real hosts." };
    }

    private static async Task<object> Idle(int seconds)
    {
        using var f = new CoreFixture(100);
        // Warm the update path before the one-shot idle wait; measurement tooling only.
        for (var i = 0; i < 10_000; i++) f.Update();
        await Task.Delay(TimeSpan.FromSeconds(2));
        var process = Process.GetCurrentProcess();
        var trials = new List<object>();
        for (var trial = 1; trial <= 3; trial++)
        {
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var start = Stopwatch.GetTimestamp();
            // Exactly one harness endpoint timer per trial, no Engine timer and no sampling loop.
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var bytes = GC.GetTotalAllocatedBytes() - allocated;
            var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray();
            process.Refresh();
            trials.Add(new
            {
                Trial = trial, WallSeconds = wall, CpuMs = cpuMs, CpuMsPerSecond = cpuMs / wall,
                SingleCorePercent = cpuMs / wall / 10,
                MachinePercent = cpuMs / wall / 10 / Environment.ProcessorCount,
                AllocatedBytes = bytes, Collections = collections, PrivateBytes = process.PrivateMemorySize64,
                EngineScheduleCalls = f.Ports.ScheduleCalls, EngineExecutorCalls = f.Ports.ExecutionCalls,
                HarnessEndpointTimers = 1
            });
            f.Ports.Advance(TimeSpan.FromDays(1));
        }
        if (f.Ports.ScheduleCalls != 0 || f.Ports.ExecutionCalls != 0) throw new InvalidOperationException("Idle scheduled work.");
        return new { Rules = 100, RealProviders = 0, WarmupSeconds = 2, Trials = trials,
            Limitation = "Short baseline; CPU includes runtime/harness. No ETW wakeup/I/O or one-second CPU percentiles measured. Not a V1 acceptance run." };
    }

    private static async Task<object> Matrix(int seconds)
    {
        var rows = new List<object>();
        foreach (var rules in new[] { 100, 1000, 10000 })
        foreach (var rate in new[] { 100, 1000 })
        {
            using var f = new CoreFixture(rules, subscribe: true);
            for (var i = 0; i < 10_000; i++) f.Deliver();
            var count = seconds * rate;
            var samples = new double[count];
            var batch = rate / 100;
            var timerWaits = 0;
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes();
            var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            var start = Stopwatch.GetTimestamp();
            for (var offset = 0; offset < count; offset += batch)
            {
                // Bounded input generator: 10 ms batches, deadlines only for the finite test trace.
                var deadline = TimeSpan.FromSeconds((double)(offset + batch) / rate);
                var remaining = deadline - Stopwatch.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero)
                {
                    timerWaits++;
                    await Task.Delay(remaining);
                }
                f.Ports.Advance(TimeSpan.FromMilliseconds(10));
                for (var n = 0; n < batch; n++)
                {
                    var eventStart = Stopwatch.GetTimestamp();
                    f.Deliver();
                    samples[offset + n] = Stopwatch.GetElapsedTime(eventStart).TotalMicroseconds;
                }
            }
            var wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var bytes = GC.GetTotalAllocatedBytes() - allocated;
            rows.Add(new
            {
                Rules = rules, RequestedEventsPerSecond = rate, Events = count, WallSeconds = wall,
                ActualEventsPerSecond = count / wall, CpuMs = cpuMs, CpuMsPerSecond = cpuMs / wall,
                SingleCorePercent = cpuMs / wall / 10, MachinePercent = cpuMs / wall / 10 / Environment.ProcessorCount,
                ProcessingP95Microseconds = Percentile(samples, .95), ProcessingP99Microseconds = Percentile(samples, .99),
                AllocatedBytes = bytes, Collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray(),
                CandidatesPerEvent = 10, f.Subscription!.HighWatermark, f.Subscription.Dropped,
                EngineScheduleCalls = f.Ports.ScheduleCalls, GeneratorTimerWaits = timerWaits
            });
        }
        using var storm = new CoreFixture(10000, subscribe: true, capacity: 256);
        var stormStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < 100_000; i++) storm.Update();
        var stormWall = Stopwatch.GetElapsedTime(stormStart).TotalSeconds;
        if (!storm.Subscription!.TryRead(out var gap) || gap!.Kind != DeliveryKind.SnapshotRequired ||
            storm.Subscription.HighWatermark > 256 || storm.Engine.ActiveSubscriptions != 0)
            throw new InvalidOperationException("Storm was not bounded or gap not delivered.");
        return new
        {
            Rows = rows,
            Storm = new { Events = 100_000, WallSeconds = stormWall, Capacity = 256, storm.Subscription.HighWatermark,
                DiscardedAtDisconnect = storm.Subscription.Dropped, Gap = gap.Reason, ActiveSubscriptions = storm.Engine.ActiveSubscriptions },
            Limitation = "Processing latency is update→selection→dequeue, excludes generator wait and OS/provider/Intent latency. CPU includes finite generator timers; p95/p99 are not CPU windows. Storm detaches slow consumer; later updates are not delivered."
        };
    }
}
