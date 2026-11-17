using System.Data.SqlTypes;
using JasperFx.Core;
using Shouldly;

namespace CoreTests.Core;

public class SequentialGuidTests
{
    private static readonly byte[] Worker = [1, 2, 3, 4];

    // PostgreSQL compares a uuid byte by byte in its RFC (big-endian) form
    private static int comparePostgres(Guid x, Guid y)
        => x.ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(y.ToByteArray(bigEndian: true));

    // SqlGuid compares the way SQL Server compares a uniqueidentifier
    private static int compareSqlServer(Guid x, Guid y) => new SqlGuid(x).CompareTo(new SqlGuid(y));

    private static void shouldIncreaseInPostgresOrder(IReadOnlyList<Guid> ids)
    {
        for (var i = 1; i < ids.Count; i++)
        {
            if (comparePostgres(ids[i - 1], ids[i]) >= 0 || ids[i - 1].CompareTo(ids[i]) >= 0 ||
                string.CompareOrdinal(ids[i - 1].ToString(), ids[i].ToString()) >= 0)
            {
                throw new ShouldAssertException($"Id {i} ({ids[i]}) does not sort after id {i - 1} ({ids[i - 1]})");
            }
        }
    }

    private static void shouldIncreaseInSqlServerOrder(IReadOnlyList<Guid> ids)
    {
        for (var i = 1; i < ids.Count; i++)
        {
            if (compareSqlServer(ids[i - 1], ids[i]) >= 0)
            {
                throw new ShouldAssertException($"Id {i} ({ids[i]}) does not sort after id {i - 1} ({ids[i - 1]})");
            }
        }
    }

    // The tick is the first 8 bytes of the PostgreSQL order, big-endian
    private static long tickOf(Guid postgresOrdered)
        => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(postgresOrdered.ToByteArray(bigEndian: true));

    [Fact]
    public void ids_increase_in_postgres_order()
    {
        var ids = Enumerable.Range(0, 100_000).Select(_ => SequentialGuid.NextPostgresOrder()).ToList();

        shouldIncreaseInPostgresOrder(ids);
    }

    [Fact]
    public void ids_increase_in_sql_server_order()
    {
        var ids = Enumerable.Range(0, 100_000).Select(_ => SequentialGuid.NextSqlServerOrder()).ToList();

        shouldIncreaseInSqlServerOrder(ids);
    }

    [Fact]
    public void the_generator_instances_use_the_shared_generator()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 1000; i++)
        {
            ids.Add(SequentialGuid.PostgresOrder.NewGuid());
            ids.Add(SequentialGuid.NextPostgresOrder());
        }

        shouldIncreaseInPostgresOrder(ids);

        var sqlServer = new List<Guid>();
        for (var i = 0; i < 1000; i++)
        {
            sqlServer.Add(SequentialGuid.SqlServerOrder.NewGuid());
            sqlServer.Add(SequentialGuid.NextSqlServerOrder());
        }

        shouldIncreaseInSqlServerOrder(sqlServer);
    }

    [Fact]
    public void the_comb_guid_now_comes_from_the_sequential_generator()
    {
#pragma warning disable CS0618
        var ids = Enumerable.Range(0, 10_000).Select(_ => CombGuidIdGeneration.NewGuid()).ToList();
#pragma warning restore CS0618

        shouldIncreaseInPostgresOrder(ids);
    }

    [Fact]
    public void the_tick_is_the_most_significant_part_in_both_orders()
    {
        var tick = DateTime.UtcNow.Ticks;
        var generator = new SequentialGuidGenerator(Worker, 77, () => tick);

        tickOf(generator.NextPostgresOrder()).ShouldBe(tick);

        // SQL Server compares bytes 10-15 and then 8-9 first
        var bytes = generator.NextSqlServerOrder().ToByteArray();
        byte[] tickBytes = [bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15], bytes[8], bytes[9]];
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(tickBytes).ShouldBe(tick);
    }

    [Fact]
    public void the_worker_and_process_id_are_in_the_id()
    {
        var generator = new SequentialGuidGenerator(Worker, 0x12345678, () => 1000);

        var bytes = generator.NextPostgresOrder().ToByteArray(bigEndian: true);
        bytes[8..12].ShouldBe(Worker);
        bytes[12..14].ShouldBe(new byte[] { 0x56, 0x78 });
    }

    [Fact]
    public void the_shared_generator_carries_this_process_id()
    {
        var bytes = SequentialGuid.NextPostgresOrder().ToByteArray(bigEndian: true);

        var processId = Environment.ProcessId;
        bytes[12].ShouldBe((byte)(processId >> 8));
        bytes[13].ShouldBe((byte)processId);
    }

    [Fact]
    public void ids_within_one_tick_increase_past_255()
    {
        // The sequence has to be big-endian in both orders, or the 257th id of a tick sorts before the 256th
        var generator = new SequentialGuidGenerator(Worker, 1, () => 5_000_000);

        var postgres = Enumerable.Range(0, 1000).Select(_ => generator.NextPostgresOrder()).ToList();
        shouldIncreaseInPostgresOrder(postgres);
        postgres.Select(tickOf).Distinct().ShouldBe(new[] { 5_000_000L });

        var sqlServer = Enumerable.Range(0, 1000).Select(_ => generator.NextSqlServerOrder()).ToList();
        shouldIncreaseInSqlServerOrder(sqlServer);
    }

    [Fact]
    public void a_full_sequence_rolls_over_into_the_next_tick()
    {
        const long tick = 5_000_000;
        var generator = new SequentialGuidGenerator(Worker, 1, () => tick);

        var ids = Enumerable.Range(0, SequentialGuidGenerator.MaxSequence + 1 + 100)
            .Select(_ => generator.NextPostgresOrder()).ToList();

        shouldIncreaseInPostgresOrder(ids);
        ids.Distinct().Count().ShouldBe(ids.Count);
        tickOf(ids[SequentialGuidGenerator.MaxSequence]).ShouldBe(tick);
        tickOf(ids[SequentialGuidGenerator.MaxSequence + 1]).ShouldBe(tick + 1);

        // The clock catching up to the borrowed tick does not hand out a borrowed id again
        var later = new List<Guid>(ids) { generator.NextPostgresOrder() };
        shouldIncreaseInPostgresOrder(later);
    }

    [Fact]
    public void a_clock_that_goes_backwards_does_not_go_back_in_order()
    {
        var now = 10_000_000L;
        var generator = new SequentialGuidGenerator(Worker, 1, () => now);

        var ids = new List<Guid>();
        var sqlServer = new List<Guid>();
        for (var i = 0; i < 100; i++)
        {
            ids.Add(generator.NextPostgresOrder());
            sqlServer.Add(generator.NextSqlServerOrder());
            now += i % 3 == 0 ? -1_000 : 700;
        }

        // ... including back past the moment the generator was created
        now = 0;
        ids.Add(generator.NextPostgresOrder());
        sqlServer.Add(generator.NextSqlServerOrder());

        shouldIncreaseInPostgresOrder(ids);
        shouldIncreaseInSqlServerOrder(sqlServer);
    }

    [Fact]
    public void a_generator_that_outlives_the_counter_range_keeps_increasing()
    {
        const long start = 1_000_000;
        var now = start;
        var generator = new SequentialGuidGenerator(Worker, 1, () => now);

        var postgres = new List<Guid>();
        var sqlServer = new List<Guid>();

        void draw(int count)
        {
            for (var i = 0; i < count; i++)
            {
                postgres.Add(generator.NextPostgresOrder());
                sqlServer.Add(generator.NextSqlServerOrder());
            }
        }

        draw(10);

        // The last tick the counter can hold, then past it
        now = start + SequentialGuidGenerator.MaxOffset;
        draw(10);
        tickOf(postgres[^1]).ShouldBe(now);

        now += 5;
        draw(10);
        tickOf(postgres[^1]).ShouldBe(now);

        // A clock going backwards after the overflow
        now -= 3;
        draw(10);

        // A full sequence in one tick after the overflow
        draw(SequentialGuidGenerator.MaxSequence);

        shouldIncreaseInPostgresOrder(postgres);
        shouldIncreaseInSqlServerOrder(sqlServer);
        postgres.Concat(sqlServer).Distinct().Count().ShouldBe(postgres.Count + sqlServer.Count);
    }

    [Fact]
    public void a_used_up_counter_carries_on_past_it()
    {
        // The counter's last tick, with every sequence value in it drawn
        const long start = 1_000_000;
        const long last = start + SequentialGuidGenerator.MaxOffset;
        var now = start;
        var generator = new SequentialGuidGenerator(Worker, 1, () => now);
        now = last;

        var ids = Enumerable.Range(0, SequentialGuidGenerator.MaxSequence + 1 + 10)
            .Select(_ => generator.NextPostgresOrder()).ToList();

        shouldIncreaseInPostgresOrder(ids);
        tickOf(ids[SequentialGuidGenerator.MaxSequence]).ShouldBe(last);
        tickOf(ids[^1]).ShouldBe(last + 1);
    }

    [Fact]
    public void processes_on_one_machine_with_the_same_worker_id_draw_different_ids()
    {
        // The NewId collision wolverine#5399 hit: same worker id, same moment, same sequence
        const long tick = 7_000_000;
        var one = new SequentialGuidGenerator(Worker, 100, () => tick);
        var two = new SequentialGuidGenerator(Worker, 101, () => tick);

        var ids = Enumerable.Range(0, 1000).SelectMany(_ => new[]
        {
            one.NextPostgresOrder(), two.NextPostgresOrder(), one.NextSqlServerOrder(), two.NextSqlServerOrder()
        }).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public void processes_with_different_worker_ids_draw_different_ids()
    {
        // Containers commonly all run as process 1
        const long tick = 7_000_000;
        var one = new SequentialGuidGenerator([1, 2, 3, 4], 1, () => tick);
        var two = new SequentialGuidGenerator([1, 2, 3, 5], 1, () => tick);

        var ids = Enumerable.Range(0, 1000).SelectMany(_ => new[]
        {
            one.NextPostgresOrder(), two.NextPostgresOrder(), one.NextSqlServerOrder(), two.NextSqlServerOrder()
        }).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public void the_same_worker_and_process_at_the_same_moment_collide()
    {
        // What keeps processes apart is the worker and process id, and nothing else
        const long tick = 7_000_000;
        var one = new SequentialGuidGenerator(Worker, 1, () => tick);
        var two = new SequentialGuidGenerator(Worker, 1, () => tick);

        one.NextPostgresOrder().ShouldBe(two.NextPostgresOrder());
    }

    [Fact]
    public void each_generator_draws_its_own_random_worker_id()
    {
        var one = new SequentialGuidGenerator().NextPostgresOrder().ToByteArray(bigEndian: true);
        var two = new SequentialGuidGenerator().NextPostgresOrder().ToByteArray(bigEndian: true);

        // 1 in 2^32 to fail
        Convert.ToHexString(one[8..12]).ShouldNotBe(Convert.ToHexString(two[8..12]));
    }
}
