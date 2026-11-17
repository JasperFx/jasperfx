using BenchmarkDotNet.Attributes;
using JasperFx.Core;

namespace CommandLineBenchmarks;

// dotnet run -c Release --project src/CommandLineBenchmarks -- --filter *SequentialGuid*
// One thread per call. Guid.CreateVersion7() and the old COMB both draw from the OS random number
// generator on every id; SequentialGuid does not.
[MemoryDiagnoser]
public class SequentialGuidBenchmarks
{
    [Benchmark(Baseline = true)]
    public Guid CreateVersion7() => Guid.CreateVersion7();

    [Benchmark]
    public Guid NewGuid() => Guid.NewGuid();

    [Benchmark]
    public Guid OldComb() => CombGuid.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Benchmark]
    public Guid PostgresOrder() => SequentialGuid.NextPostgresOrder();

    [Benchmark]
    public Guid SqlServerOrder() => SequentialGuid.NextSqlServerOrder();

    [Benchmark]
    public Guid PostgresOrderThroughTheInterface() => SequentialGuid.PostgresOrder.NewGuid();
}

// 16 threads drawing ids at once from the one shared generator
[MemoryDiagnoser]
public class SequentialGuidContentionBenchmarks
{
    private const int Threads = 16;
    private const int PerThread = 10_000;

    private static void drawOn16Threads(Func<Guid> next)
    {
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            for (var i = 0; i < PerThread; i++) next();
        });
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Threads * PerThread)]
    public void CreateVersion7() => drawOn16Threads(Guid.CreateVersion7);

    [Benchmark(OperationsPerInvoke = Threads * PerThread)]
    public void PostgresOrder() => drawOn16Threads(SequentialGuid.NextPostgresOrder);

    [Benchmark(OperationsPerInvoke = Threads * PerThread)]
    public void SqlServerOrder() => drawOn16Threads(SequentialGuid.NextSqlServerOrder);
}

// What CombGuidIdGeneration.NewGuid() did before it delegated to SequentialGuid
internal static class CombGuid
{
#pragma warning disable CS0618
    public static Guid Create(Guid value, DateTimeOffset timestamp) => CombGuidIdGeneration.Create(value, timestamp);
#pragma warning restore CS0618
}
