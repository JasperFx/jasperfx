using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region String-keyed stream events and aggregate

public record StringLedgerOpened(string Owner);

public record StringLedgerEntryPosted(decimal Amount);

public record StringLedgerAudited(string Auditor);

/// <summary>
/// A plain self-aggregating type keyed by the stream's string key. The subject of this suite is the
/// stream identity, not the aggregation conventions, so the folding is as dull as possible.
/// </summary>
public partial class StringLedger
{
    public string Id { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public int EntryCount { get; set; }

    public static StringLedger Create(StringLedgerOpened e) => new() { Owner = e.Owner };

    public void Apply(StringLedgerEntryPosted e)
    {
        Balance += e.Amount;
        EntryCount++;
    }
}

#endregion

/// <summary>
/// The event store operations surface against <see cref="StreamIdentity.AsString"/> streams — the
/// string-keyed counterpart to <c>StreamReadCompliance</c>, <c>FetchForWritingCompliance</c> and
/// <c>StreamArchivingCompliance</c>, all of which are Guid-only and say so.
/// </summary>
/// <remarks>
/// <para>
/// Stream identity is a store-level setting, so string-keyed behavior cannot be folded into those
/// suites — it needs its own store, and therefore its own suite. Until this existed, the only shared
/// coverage of string identity was
/// <see cref="StringIdentitySingleStreamCompliance{TFixture,TOperations,TQuerySession}"/>, which
/// exercises single stream *projections* and nothing on the read/write surface underneath them.
/// </para>
/// <para>
/// This is the seam where a store is most likely to have implemented one identity well and the other
/// by analogy. Every operation here has a <c>string</c> overload declared right next to the
/// <c>Guid</c> one on <see cref="IEventStoreOperations"/> or <c>IQueryEventStore</c>, so nothing here
/// needs a fixture member — which is exactly why an untested gap can sit in a store for a long time
/// without anyone noticing.
/// </para>
/// <para>
/// No capability gates. A store that declares <see cref="StreamIdentity.AsString"/> owes the same
/// contract on the same methods as it does for Guids; the only thing that changes is which column
/// carries the identity.
/// </para>
/// </remarks>
public abstract class StringStreamIdentityCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_string_streams";
        config.StreamIdentity = StreamIdentity.AsString;

        config.AddEventType<StringLedgerOpened>();
        config.AddEventType<StringLedgerEntryPosted>();
        config.AddEventType<StringLedgerAudited>();

        config.Snapshot<StringLedger>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// Keys deliberately carry a slash and a dash. Both are legal in a stream key and neither is
    /// special to a store, but an implementation that ever interpolates a key into SQL rather than
    /// parameterizing it tends to fall over on punctuation first.
    /// </summary>
    private static string streamKey() => $"ledger/{Guid.NewGuid():N}-01";

    private async Task<string> aLedgerAsync(params object[] events)
    {
        var key = streamKey();

        await using var session = OpenSession();
        var all = new List<object> { new StringLedgerOpened("Hilda") };
        all.AddRange(events);
        EventsFor(session).StartStream<StringLedger>(key, all);
        await SaveChangesAsync(session);

        return key;
    }

    [Fact]
    public async Task start_stream_and_read_it_back_by_key()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(25), new StringLedgerEntryPosted(75));

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(key, token: Cancellation);

        events.Count.ShouldBe(3);
        events.Select(x => x.Version).ToArray().ShouldBe(new[] { 1L, 2L, 3L });
        events[0].Data.ShouldBeOfType<StringLedgerOpened>();
    }

    [Fact]
    public async Task appending_to_an_existing_string_keyed_stream_continues_its_versions()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(10));

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(key, new StringLedgerEntryPosted(5));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(key, token: Cancellation);

        events.Count.ShouldBe(3);
        events.Last().Version.ShouldBe(3);
    }

    [Fact]
    public async Task events_on_a_string_keyed_stream_carry_the_key()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(1));

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(key, token: Cancellation);

        events.ShouldAllBe(x => x.StreamKey == key);

        // The negative half of the contract, which neither product asserted anywhere before this
        // suite: on a string-keyed stream the Guid slot is left at its default rather than being
        // filled with a synthesized value. Consumers that branch on identity (CritterWatch reads
        // these envelopes cross-store) need to be able to tell which slot is authoritative.
        events.ShouldAllBe(x => x.StreamId == Guid.Empty);
    }

    [Fact]
    public async Task stream_state_reports_the_key_and_version()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(1), new StringLedgerEntryPosted(2));

        await using var query = OpenSession();
        var state = await EventsFor(query).FetchStreamStateAsync(key, Cancellation);

        state.ShouldNotBeNull();
        state.Key.ShouldBe(key);
        state.Version.ShouldBe(3);
    }

    [Fact]
    public async Task stream_state_for_an_unknown_key_is_null()
    {
        await using var query = OpenSession();
        var state = await EventsFor(query).FetchStreamStateAsync(streamKey(), Cancellation);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task fetch_stream_bounded_by_version_on_a_string_key()
    {
        var key = await aLedgerAsync(
            new StringLedgerEntryPosted(10),
            new StringLedgerEntryPosted(20),
            new StringLedgerEntryPosted(30));

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(key, 2, token: Cancellation);

        events.Count.ShouldBe(2);
        events.Last().Version.ShouldBe(2);
    }

    [Fact]
    public async Task fetch_stream_from_a_starting_version_on_a_string_key()
    {
        var key = await aLedgerAsync(
            new StringLedgerEntryPosted(10),
            new StringLedgerEntryPosted(20),
            new StringLedgerEntryPosted(30));

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(key, fromVersion: 3, token: Cancellation);

        events.ShouldAllBe(x => x.Version >= 3);
        events.Count.ShouldBe(2);
    }

    [Fact]
    public async Task aggregate_a_string_keyed_stream_live()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(40), new StringLedgerEntryPosted(2));

        await using var query = OpenSession();
        var ledger = await EventsFor(query).AggregateStreamAsync<StringLedger>(key, token: Cancellation);

        ledger.ShouldNotBeNull();
        ledger.Owner.ShouldBe("Hilda");
        ledger.Balance.ShouldBe(42);
        ledger.EntryCount.ShouldBe(2);
    }

    [Fact]
    public async Task aggregate_a_string_keyed_stream_at_an_earlier_version()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(40), new StringLedgerEntryPosted(2));

        await using var query = OpenSession();
        var ledger = await EventsFor(query).AggregateStreamAsync<StringLedger>(key, 2, token: Cancellation);

        ledger.ShouldNotBeNull();
        ledger.Balance.ShouldBe(40);
        ledger.EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task aggregate_an_unknown_string_key_is_null()
    {
        await using var query = OpenSession();
        var ledger = await EventsFor(query).AggregateStreamAsync<StringLedger>(streamKey(), token: Cancellation);

        ledger.ShouldBeNull();
    }

    [Fact]
    public async Task fetch_for_writing_a_string_keyed_stream_that_does_not_exist_yet()
    {
        var key = streamKey();

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<StringLedger>(key, Cancellation);

        stream.Aggregate.ShouldBeNull();
        stream.Key.ShouldBe(key);
        stream.StartingVersion.ShouldBe(0);
    }

    [Fact]
    public async Task fetch_for_writing_an_existing_string_keyed_stream()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(15));

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<StringLedger>(key, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(15);
        stream.Key.ShouldBe(key);
        stream.StartingVersion.ShouldBe(2);

        stream.AppendOne(new StringLedgerEntryPosted(5));
        await SaveChangesAsync(session);

        await using var query = OpenSession();
        var ledger = await EventsFor(query).AggregateStreamAsync<StringLedger>(key, token: Cancellation);
        ledger!.Balance.ShouldBe(20);
    }

    [Fact]
    public async Task fetch_for_writing_a_string_key_with_a_stale_expected_version_fails()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(15));

        // As in the Guid suite: wrap the whole fetch-append-save sequence and assert the shared
        // ConcurrencyException base, so a store is free to detect the conflict eagerly or at commit.
        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();
            var stream = await EventsFor(session).FetchForWriting<StringLedger>(key, 1, Cancellation);
            stream.AppendOne(new StringLedgerEntryPosted(1));
            await SaveChangesAsync(session);
        });
    }

    [Fact]
    public async Task fetch_for_exclusive_writing_a_string_keyed_stream()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(15));

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForExclusiveWriting<StringLedger>(key, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(15);
        stream.Key.ShouldBe(key);

        stream.AppendOne(new StringLedgerEntryPosted(7));
        await SaveChangesAsync(session);

        await using var query = OpenSession();
        var ledger = await EventsFor(query).AggregateStreamAsync<StringLedger>(key, token: Cancellation);
        ledger!.Balance.ShouldBe(22);
    }

    [Fact]
    public async Task write_to_aggregate_by_string_key()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(100));

        await using (var session = OpenSession())
        {
            await EventsFor(session).WriteToAggregate<StringLedger>(key, stream =>
            {
                stream.Aggregate.ShouldNotBeNull();
                stream.Aggregate.Balance.ShouldBe(100);
                stream.AppendOne(new StringLedgerEntryPosted(-40));
            }, Cancellation);
        }

        await using var query = OpenSession();
        var ledger = await EventsFor(query).AggregateStreamAsync<StringLedger>(key, token: Cancellation);
        ledger!.Balance.ShouldBe(60);
    }

    [Fact]
    public async Task append_optimistic_against_an_existing_string_keyed_stream()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(10));

        await using (var session = OpenSession())
        {
            await EventsFor(session).AppendOptimistic(key, new StringLedgerEntryPosted(5));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var state = await EventsFor(query).FetchStreamStateAsync(key, Cancellation);
        state!.Version.ShouldBe(3);
    }

    [Fact]
    public async Task append_exclusive_against_an_existing_string_keyed_stream()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(10));

        await using (var session = OpenSession())
        {
            await EventsFor(session).AppendExclusive(key, new StringLedgerEntryPosted(5));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var state = await EventsFor(query).FetchStreamStateAsync(key, Cancellation);
        state!.Version.ShouldBe(3);
    }

    [Fact]
    public async Task fetch_latest_by_string_key()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(11), new StringLedgerEntryPosted(31));

        await using var session = OpenSession();
        var ledger = await EventsFor(session).FetchLatest<StringLedger>(key, Cancellation);

        ledger.ShouldNotBeNull();
        ledger.Balance.ShouldBe(42);
    }

    [Fact]
    public async Task archive_a_string_keyed_stream()
    {
        var key = await aLedgerAsync(new StringLedgerEntryPosted(1));

        await using (var reader = OpenSession())
        {
            var before = await EventsFor(reader).FetchStreamStateAsync(key, Cancellation);
            before!.IsArchived.ShouldBeFalse();
        }

        await using (var session = OpenSession())
        {
            EventsFor(session).ArchiveStream(key);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var after = await EventsFor(query).FetchStreamStateAsync(key, Cancellation);

        after.ShouldNotBeNull();
        after.Key.ShouldBe(key);
        after.IsArchived.ShouldBeTrue();
    }
}
