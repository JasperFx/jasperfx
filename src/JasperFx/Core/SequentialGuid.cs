using System.Security.Cryptography;

namespace JasperFx.Core;

/// <summary>
///     Makes new Guid identities
/// </summary>
public interface IGuidGenerator
{
    Guid NewGuid();
}

/// <summary>
///     Time-ordered Guids that cost no random number per id, in the sort order of the database they are written to.
///     Use these instead of <c>Guid.CreateVersion7()</c> or a COMB Guid for keys that go into a b-tree index.
/// </summary>
/// <remarks>
///     <para>
///         The layout is MassTransit's NewId: a 100 ns timestamp, a 6-byte worker id and a 16-bit sequence.
///         Within one process every id sorts after the one before it, so inserts append at the right edge of the
///         index instead of splitting pages in the middle of it. <c>Guid.CreateVersion7()</c> and COMB Guids are
///         only ordered by millisecond and random within it, which leaves a busy PostgreSQL primary key a third
///         bigger, and neither is ordered at all for SQL Server.
///     </para>
///     <para>
///         The worker id is 6 random bytes drawn once per process (never the machine's MAC address or the process
///         id, which is often 1 for every process in a container), so two processes only draw the same ids if they
///         draw the same 48 random bits. An id does show its creation time, to 100 ns.
///     </para>
///     <para>
///         The two orders come from the same generator, so ids of either order are unique against each other.
///     </para>
/// </remarks>
public static class SequentialGuid
{
    private static readonly SequentialGuidGenerator _shared = new();

    /// <summary>
    ///     Ids ordered the way PostgreSQL compares a <c>uuid</c>. The same order as <see cref="Guid.CompareTo(Guid)" />
    ///     and as the lowercase text form, so this is also the order for SQLite and any database that stores the id
    ///     as text.
    /// </summary>
    public static IGuidGenerator PostgresOrder { get; } = new PostgresOrderGenerator(_shared);

    /// <summary>
    ///     Ids ordered the way SQL Server compares a <c>uniqueidentifier</c> (the last six bytes first).
    /// </summary>
    public static IGuidGenerator SqlServerOrder { get; } = new SqlServerOrderGenerator(_shared);

    /// <summary>
    ///     The next id in PostgreSQL <c>uuid</c> order. See <see cref="PostgresOrder" />.
    /// </summary>
    public static Guid NextPostgresOrder() => _shared.NextPostgresOrder();

    /// <summary>
    ///     The next id in SQL Server <c>uniqueidentifier</c> order. See <see cref="SqlServerOrder" />.
    /// </summary>
    public static Guid NextSqlServerOrder() => _shared.NextSqlServerOrder();

    private sealed class PostgresOrderGenerator(SequentialGuidGenerator generator) : IGuidGenerator
    {
        public Guid NewGuid() => generator.NextPostgresOrder();
    }

    private sealed class SqlServerOrderGenerator(SequentialGuidGenerator generator) : IGuidGenerator
    {
        public Guid NewGuid() => generator.NextSqlServerOrder();
    }
}

/// <summary>
///     The generator behind <see cref="SequentialGuid" />. The tick and the sequence live in one 64-bit counter that
///     every thread advances with a compare-and-swap: the tick as an offset from the tick the generator was created at
///     (47 bits, about 163 days) above the 16-bit sequence. A later tick starts the sequence again, a full sequence
///     borrows the next tick, and a clock that goes backwards keeps counting from where it was, so the ids of one
///     generator always increase. A generator that outlives the offset range carries on under a lock, with ticks later
///     than anything the counter handed out.
/// </summary>
/// <remarks>
///     The 16 bytes, most significant first: the tick (8 bytes, big-endian), the worker id (6 bytes) and the sequence
///     (2 bytes, big-endian). <see cref="NextPostgresOrder" /> lays them out
///     in that order; <see cref="NextSqlServerOrder" /> puts them where SQL Server compares them, which is byte
///     positions 10-15, 8-9, 6-7, 4-5 and then 0-3 of <see cref="Guid.ToByteArray()" />.
/// </remarks>
internal sealed class SequentialGuidGenerator
{
    internal const int SequenceBits = 16;
    internal const int MaxSequence = (1 << SequenceBits) - 1;

    /// <summary>
    ///     The largest tick offset the counter holds: offset &lt;&lt; 16 | sequence has to stay a positive long
    /// </summary>
    internal const long MaxOffset = (1L << (63 - SequenceBits)) - 1;

    private readonly Func<long>? _clock;
    private readonly long _baseTick;

    internal const int WorkerIdLength = 6;

    // The worker id, in the order it is compared
    private readonly byte _w0, _w1, _w2, _w3, _w4, _w5;

    // (tick - _baseTick) << 16 | sequence, one below the first id
    private long _state = -1;

    private readonly object _overflowLock = new();
    private long _overflowTick = long.MinValue;
    private int _overflowSequence;

    public SequentialGuidGenerator() : this(randomWorkerId(), null)
    {
    }

    /// <param name="workerId">Exactly 6 bytes</param>
    /// <param name="clock">The current time in ticks, <see cref="DateTime.UtcNow" /> when null</param>
    internal SequentialGuidGenerator(ReadOnlySpan<byte> workerId, Func<long>? clock)
    {
        if (workerId.Length != WorkerIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), $"The worker id has to be {WorkerIdLength} bytes");
        }

        _clock = clock;
        _baseTick = now();

        _w0 = workerId[0];
        _w1 = workerId[1];
        _w2 = workerId[2];
        _w3 = workerId[3];
        _w4 = workerId[4];
        _w5 = workerId[5];
    }

    private static byte[] randomWorkerId() => RandomNumberGenerator.GetBytes(WorkerIdLength);

    private long now() => _clock?.Invoke() ?? DateTime.UtcNow.Ticks;

    private void next(out long tick, out int sequence)
    {
        var offset = now() - _baseTick;
        if (offset < 0) offset = 0;

        if (offset <= MaxOffset)
        {
            var floor = offset << SequenceBits;
            while (true)
            {
                var current = Volatile.Read(ref _state);
                var candidate = floor > current ? floor : current + 1;

                // current + 1 past long.MaxValue goes negative: the counter is used up
                if (candidate < 0) break;

                if (Interlocked.CompareExchange(ref _state, candidate, current) == current)
                {
                    tick = _baseTick + (candidate >> SequenceBits);
                    sequence = (int)(candidate & MaxSequence);
                    return;
                }
            }
        }

        nextAfterOverflow(offset, out tick, out sequence);
    }

    private void nextAfterOverflow(long offset, out long tick, out int sequence)
    {
        lock (_overflowLock)
        {
            // Every tick here is later than the last tick the counter can hold
            tick = _baseTick + Math.Max(offset, MaxOffset + 1);

            if (tick <= _overflowTick)
            {
                tick = _overflowTick;
                if (_overflowSequence == MaxSequence)
                {
                    tick++;
                    sequence = 0;
                }
                else
                {
                    sequence = _overflowSequence + 1;
                }
            }
            else
            {
                sequence = 0;
            }

            _overflowTick = tick;
            _overflowSequence = sequence;
        }
    }

    public Guid NextPostgresOrder()
    {
        next(out var tick, out var sequence);

        return new Guid(
            (int)(tick >> 32),
            (short)(tick >> 16),
            (short)tick,
            _w0, _w1, _w2, _w3, _w4, _w5,
            (byte)(sequence >> 8), (byte)sequence);
    }

    public Guid NextSqlServerOrder()
    {
        next(out var tick, out var sequence);

        // Guid(int a, short b, short c, ...) writes a, b and c little-endian into bytes 0-3, 4-5 and 6-7
        return new Guid(
            _w4 | (_w5 << 8) | ((sequence >> 8) << 16) | ((sequence & 0xFF) << 24),
            (short)(_w2 | (_w3 << 8)),
            (short)(_w0 | (_w1 << 8)),
            (byte)(tick >> 8), (byte)tick,
            (byte)(tick >> 56), (byte)(tick >> 48), (byte)(tick >> 40), (byte)(tick >> 32),
            (byte)(tick >> 24), (byte)(tick >> 16));
    }
}
