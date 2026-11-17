using System.Data.SqlTypes;
using System.Security.Cryptography;
using JasperFx.Core;
using Shouldly;

namespace CoreTests.Core;

public class SequentialGuidTests
{
    private static readonly byte[] Worker = [1, 2, 3, 4, 5, 6];

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
        var generator = new SequentialGuidGenerator(Worker, () => tick);

        tickOf(generator.NextPostgresOrder()).ShouldBe(tick);

        // SQL Server compares bytes 10-15 and then 8-9 first
        var bytes = generator.NextSqlServerOrder().ToByteArray();
        byte[] tickBytes = [bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15], bytes[8], bytes[9]];
        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(tickBytes).ShouldBe(tick);
    }

    [Fact]
    public void the_worker_id_is_in_the_id_in_both_orders()
    {
        var generator = new SequentialGuidGenerator(Worker, () => 1000);

        generator.NextPostgresOrder().ToByteArray(bigEndian: true)[8..14].ShouldBe(Worker);

        // SQL Server compares bytes 6-7, then 4-5, then 0-3 after the tick
        var bytes = generator.NextSqlServerOrder().ToByteArray();
        new[] { bytes[6], bytes[7], bytes[4], bytes[5], bytes[0], bytes[1] }.ShouldBe(Worker);
    }

    [Fact]
    public void the_shared_generator_uses_one_worker_id()
    {
        var one = SequentialGuid.NextPostgresOrder().ToByteArray(bigEndian: true);
        var two = SequentialGuid.NextPostgresOrder().ToByteArray(bigEndian: true);

        one[8..14].ShouldBe(two[8..14]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void the_worker_id_has_to_be_six_bytes(int length)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SequentialGuidGenerator(new byte[length], null));
    }

    [Fact]
    public void ids_within_one_tick_increase_past_255()
    {
        // The sequence has to be big-endian in both orders, or the 257th id of a tick sorts before the 256th
        var generator = new SequentialGuidGenerator(Worker, () => 5_000_000);

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
        var generator = new SequentialGuidGenerator(Worker, () => tick);

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
        var generator = new SequentialGuidGenerator(Worker, () => now);

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
        var generator = new SequentialGuidGenerator(Worker, () => now);

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
        var generator = new SequentialGuidGenerator(Worker, () => now);
        now = last;

        var ids = Enumerable.Range(0, SequentialGuidGenerator.MaxSequence + 1 + 10)
            .Select(_ => generator.NextPostgresOrder()).ToList();

        shouldIncreaseInPostgresOrder(ids);
        tickOf(ids[SequentialGuidGenerator.MaxSequence]).ShouldBe(last);
        tickOf(ids[^1]).ShouldBe(last + 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void processes_whose_worker_ids_differ_in_any_one_byte_draw_different_ids(int index)
    {
        // Every byte of the worker id keeps two processes apart, in both orders. Containers commonly
        // all run as process 1, which is why the process id is no longer part of the id
        const long tick = 7_000_000;
        var other = Worker.ToArray();
        other[index]++;

        var one = new SequentialGuidGenerator(Worker, () => tick);
        var two = new SequentialGuidGenerator(other, () => tick);

        var ids = Enumerable.Range(0, 1000).SelectMany(_ => new[]
        {
            one.NextPostgresOrder(), two.NextPostgresOrder(), one.NextSqlServerOrder(), two.NextSqlServerOrder()
        }).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public void many_processes_at_the_same_moment_draw_different_ids()
    {
        // The NewId collision wolverine#5399 hit: many processes, same moment, same sequence. Each
        // "process" draws its own random worker id, as the shared generator does once per process
        const long tick = 7_000_000;
        var generators = Enumerable.Range(0, 200)
            .Select(_ => new SequentialGuidGenerator(RandomNumberGenerator.GetBytes(6), () => tick))
            .ToList();

        var ids = new List<Guid>();
        for (var i = 0; i < 500; i++)
        {
            foreach (var generator in generators)
            {
                ids.Add(generator.NextPostgresOrder());
                ids.Add(generator.NextSqlServerOrder());
            }
        }

        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public void the_same_worker_at_the_same_moment_collides()
    {
        // What keeps processes apart is the worker id, and nothing else
        const long tick = 7_000_000;
        var one = new SequentialGuidGenerator(Worker, () => tick);
        var two = new SequentialGuidGenerator(Worker, () => tick);

        one.NextPostgresOrder().ShouldBe(two.NextPostgresOrder());
    }

    [Fact]
    public void each_generator_draws_its_own_random_worker_id()
    {
        var one = new SequentialGuidGenerator().NextPostgresOrder().ToByteArray(bigEndian: true);
        var two = new SequentialGuidGenerator().NextPostgresOrder().ToByteArray(bigEndian: true);

        // 1 in 2^48 to fail
        Convert.ToHexString(one[8..14]).ShouldNotBe(Convert.ToHexString(two[8..14]));
    }
}
