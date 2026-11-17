using System.Data.SqlTypes;
using JasperFx.Core;
using Shouldly;

namespace StressTests;

public class SequentialGuidStressTests
{
    private const int Threads = 16;
    private const int PerThread = 100_000;

    [Fact]
    public void ids_drawn_on_16_threads_are_unique_and_each_thread_sees_them_increase()
    {
        var postgres = new Guid[Threads][];
        var sqlServer = new Guid[Threads][];
        using var start = new Barrier(Threads);

        var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            var p = new Guid[PerThread];
            var s = new Guid[PerThread];
            start.SignalAndWait();
            for (var i = 0; i < PerThread; i++)
            {
                p[i] = SequentialGuid.NextPostgresOrder();
                s[i] = SequentialGuid.NextSqlServerOrder();
            }

            postgres[t] = p;
            sqlServer[t] = s;
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        var all = new HashSet<Guid>();
        foreach (var id in postgres.SelectMany(x => x).Concat(sqlServer.SelectMany(x => x)))
        {
            if (!all.Add(id)) throw new ShouldAssertException($"{id} was drawn twice");
        }

        all.Count.ShouldBe(Threads * PerThread * 2);

        for (var t = 0; t < Threads; t++)
        {
            for (var i = 1; i < PerThread; i++)
            {
                if (postgres[t][i - 1].CompareTo(postgres[t][i]) >= 0)
                {
                    throw new ShouldAssertException($"Thread {t}: PostgreSQL order id {i} does not sort after id {i - 1}");
                }

                if (new SqlGuid(sqlServer[t][i - 1]).CompareTo(new SqlGuid(sqlServer[t][i])) >= 0)
                {
                    throw new ShouldAssertException($"Thread {t}: SQL Server order id {i} does not sort after id {i - 1}");
                }
            }
        }
    }
}
