using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using JasperFx.Events.Protected;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Stream compacting events and aggregate

public record MeterInstalled(string Location);

public record MeterRead(int Reading);

public record MeterServiced;

/// <summary>
/// Folds into a running total, so a compacted stream and an uncompacted one are distinguishable
/// only by what is stored, never by what the aggregate says.
/// </summary>
public partial class ComplianceMeter
{
    public Guid Id { get; set; }
    public string Location { get; set; } = string.Empty;
    public int Total { get; set; }
    public int ReadCount { get; set; }
    public int ServiceCount { get; set; }

    public static ComplianceMeter Create(MeterInstalled e) => new() { Location = e.Location };

    public void Apply(MeterRead e)
    {
        Total += e.Reading;
        ReadCount++;
    }

    public void Apply(MeterServiced _) => ServiceCount++;
}

/// <summary>
/// The same aggregate keyed by a string, for the string-identified half. A separate type rather
/// than a reused one because stream identity is a store-level setting and the identity type of the
/// aggregate document has to agree with it — registering a Guid-keyed document in an
/// <c>AsString</c> store is a configuration error on both products, not a runtime surprise.
/// </summary>
public partial class ComplianceStringMeter
{
    public string Id { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public int Total { get; set; }
    public int ReadCount { get; set; }
    public int ServiceCount { get; set; }

    public static ComplianceStringMeter Create(MeterInstalled e) => new() { Location = e.Location };

    public void Apply(MeterRead e)
    {
        Total += e.Reading;
        ReadCount++;
    }

    public void Apply(MeterServiced _) => ServiceCount++;
}

#endregion

/// <summary>
/// <c>CompactStreamAsync&lt;T&gt;</c> — replacing a stream's earlier events with a single
/// <c>Compacted&lt;T&gt;</c> snapshot event, without changing what the stream aggregates to.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of compacting is that it is supposed to be invisible from the read side: the
/// events go away, the answer does not change. So every test here pairs a storage assertion (how
/// many events survive, and what the survivor is) with a behavioural one (what the stream still
/// aggregates to). A store that drops the folded state, or folds it and then loses the events it
/// should have kept, fails the second half while passing the first.
/// </para>
/// <para>
/// The declaration was lifted onto <see cref="IEventStoreOperations"/> in jasperfx#635 as
/// default-implemented members that throw, so this suite reaches it through the shared operations
/// surface and needs no seam addition at all. A store without compacting would surface as a
/// <see cref="NotSupportedException"/> from the DIM rather than a compile error, which is the point
/// of that shape — see marten#5153.
/// </para>
/// <para>
/// <strong>Version preservation is the assertion most likely to catch drift.</strong> Compacting is
/// destructive to rows but must NOT rewind the stream: the stream version after compacting a
/// nine-event stream is still nine, and the surviving <c>Compacted&lt;T&gt;</c> event carries that
/// version rather than version one. Only one product's own tests pinned this before the suite
/// existed; the other asserted merely that appending afterwards still worked, which passes
/// vacuously if the version rewound and then climbed again.
/// </para>
/// <para>
/// Out of scope on purpose: the <c>Timestamp</c> cut-off on the request. Deriving a cut-off that
/// discriminates requires two commits with server-side timestamps far enough apart to order
/// reliably on a CI host that is not the database host, and the version cut-off already covers the
/// partial-compaction path deterministically.
/// </para>
/// </remarks>
public abstract class StreamCompactingCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_compacting";
        config.Snapshot<ComplianceMeter>(SnapshotLifecycle.Inline);
    };

    /// <summary>
    /// Stream identity is a store-level setting, so the string-keyed half needs its own store.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_compacting_string";
        config.StreamIdentity = StreamIdentity.AsString;
        config.Snapshot<ComplianceStringMeter>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// Nine events: one install, six reads totalling 210, two services. Deliberately more than a
    /// handful so a version cut-off can land in the middle with events on both sides.
    /// </summary>
    private static object[] theNineEvents() =>
    [
        new MeterInstalled("Substation A"),
        new MeterRead(10),
        new MeterRead(20),
        new MeterServiced(),
        new MeterRead(30),
        new MeterRead(40),
        new MeterServiced(),
        new MeterRead(50),
        new MeterRead(60)
    ];

    private async Task<Guid> aMeterAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceMeter>(streamId, theNineEvents());
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task compacting_at_the_latest_leaves_a_single_compacted_event()
    {
        var streamId = await aMeterAsync();

        await using (var session = OpenSession())
        {
            await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.Count.ShouldBe(1);
        events.Single().Data.ShouldBeOfType<Compacted<ComplianceMeter>>();
    }

    [Fact]
    public async Task the_compacted_event_carries_the_folded_snapshot()
    {
        var streamId = await aMeterAsync();

        await using (var session = OpenSession())
        {
            await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        var compacted = events.Single().Data.ShouldBeOfType<Compacted<ComplianceMeter>>();

        compacted.Snapshot.ShouldNotBeNull();
        compacted.Snapshot.Location.ShouldBe("Substation A");
        compacted.Snapshot.Total.ShouldBe(210);
        compacted.Snapshot.ReadCount.ShouldBe(6);
        compacted.Snapshot.ServiceCount.ShouldBe(2);
    }

    /// <summary>
    /// Compacting is destructive to rows but must not rewind the stream.
    /// </summary>
    [Fact]
    public async Task compacting_preserves_the_stream_version()
    {
        var streamId = await aMeterAsync();

        await using (var session = OpenSession())
        {
            await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();

        var state = await EventsFor(query).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(9);

        // ... and the survivor is stamped with that version, not with version 1.
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);
        events.Single().Version.ShouldBe(9);
    }

    [Fact]
    public async Task aggregating_a_compacted_stream_reproduces_the_pre_compaction_state()
    {
        var streamId = await aMeterAsync();

        await using (var session = OpenSession())
        {
            await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var meter = await EventsFor(query).AggregateStreamAsync<ComplianceMeter>(streamId, token: Cancellation);

        meter.ShouldNotBeNull();
        meter.Location.ShouldBe("Substation A");
        meter.Total.ShouldBe(210);
        meter.ReadCount.ShouldBe(6);
        meter.ServiceCount.ShouldBe(2);
    }

    [Fact]
    public async Task compacting_at_a_version_keeps_the_events_after_it()
    {
        var streamId = await aMeterAsync();

        await using (var session = OpenSession())
        {
            await EventsFor(session)
                .CompactStreamAsync<ComplianceMeter>(streamId, x => x.Version = 5);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        // The five compacted events collapse into one, and versions 6-9 survive untouched.
        events.Count.ShouldBe(5);

        var compacted = events[0].Data.ShouldBeOfType<Compacted<ComplianceMeter>>();
        events[0].Version.ShouldBe(5);

        // Only the first five events are folded into the snapshot: one install, three reads
        // totalling 60, one service.
        compacted.Snapshot.Total.ShouldBe(60);
        compacted.Snapshot.ReadCount.ShouldBe(3);
        compacted.Snapshot.ServiceCount.ShouldBe(1);

        events.Skip(1).Select(x => x.Version).ShouldBe(new long[] { 6, 7, 8, 9 });
    }

    [Fact]
    public async Task compacting_at_a_version_still_aggregates_to_the_whole_stream()
    {
        var streamId = await aMeterAsync();

        await using (var session = OpenSession())
        {
            await EventsFor(session)
                .CompactStreamAsync<ComplianceMeter>(streamId, x => x.Version = 5);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var meter = await EventsFor(query).AggregateStreamAsync<ComplianceMeter>(streamId, token: Cancellation);

        meter.ShouldNotBeNull();
        meter.Total.ShouldBe(210);
        meter.ReadCount.ShouldBe(6);
        meter.ServiceCount.ShouldBe(2);
    }

    [Fact]
    public async Task appending_after_compacting_continues_the_stream()
    {
        var streamId = await aMeterAsync();

        await using (var session = OpenSession())
        {
            await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId);
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new MeterRead(90));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();

        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(2);
        events.Last().Version.ShouldBe(10);

        var meter = await EventsFor(query).AggregateStreamAsync<ComplianceMeter>(streamId, token: Cancellation);
        meter.ShouldNotBeNull();
        meter.Total.ShouldBe(300);
        meter.ReadCount.ShouldBe(7);
    }

    [Fact]
    public async Task compacting_twice_is_idempotent()
    {
        var streamId = await aMeterAsync();

        for (var i = 0; i < 2; i++)
        {
            await using var session = OpenSession();
            await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();

        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(1);

        // The second pass must not re-fold the snapshot into a snapshot-of-a-snapshot, and must not
        // double-count what the first pass already folded.
        var compacted = events.Single().Data.ShouldBeOfType<Compacted<ComplianceMeter>>();
        compacted.Snapshot.Total.ShouldBe(210);
        compacted.Snapshot.ReadCount.ShouldBe(6);

        var meter = await EventsFor(query).AggregateStreamAsync<ComplianceMeter>(streamId, token: Cancellation);
        meter.ShouldNotBeNull();
        meter.Total.ShouldBe(210);
    }

    [Fact]
    public async Task compacting_a_stream_that_does_not_exist_is_a_no_op()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId);
        await SaveChangesAsync(session);

        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(0);
    }

    [Fact]
    public async Task the_archiver_sees_the_events_that_are_about_to_be_replaced()
    {
        var streamId = await aMeterAsync();
        var archiver = new RecordingArchiver();

        await using (var session = OpenSession())
        {
            await EventsFor(session).CompactStreamAsync<ComplianceMeter>(streamId, x =>
            {
                x.Version = 5;
                x.Archiver = archiver;
            });
            await SaveChangesAsync(session);
        }

        // Called once, before the destructive step, with exactly the events being replaced.
        archiver.Calls.ShouldBe(1);
        archiver.Events.Count.ShouldBe(5);
        archiver.Events.Select(x => x.Version).ShouldBe(new long[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task a_string_identified_stream_compacts_the_same_way()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var streamKey = $"meter/{Guid.NewGuid():N}";

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceStringMeter>(streamKey, theNineEvents());
            await SaveChangesAsync(session);
        }

        await using (var session = OpenSession())
        {
            await EventsFor(session).CompactStreamAsync<ComplianceStringMeter>(streamKey);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();

        var events = await EventsFor(query).FetchStreamAsync(streamKey, token: Cancellation);
        events.Count.ShouldBe(1);
        events.Single().Version.ShouldBe(9);

        var compacted = events.Single().Data.ShouldBeOfType<Compacted<ComplianceStringMeter>>();
        compacted.Snapshot.Total.ShouldBe(210);

        var meter = await EventsFor(query)
            .AggregateStreamAsync<ComplianceStringMeter>(streamKey, token: Cancellation);
        meter.ShouldNotBeNull();
        meter.Total.ShouldBe(210);
    }

    /// <summary>
    /// Nested so it can close <see cref="IEventsArchiver{TOperations}"/> over the suite's own
    /// <typeparamref name="TOperations"/> — the request carries the non-generic marker and each
    /// product downcasts to the closed generic at execution time.
    /// </summary>
    private sealed class RecordingArchiver: IEventsArchiver<TOperations>
    {
        public int Calls { get; private set; }

        public IReadOnlyList<IEvent> Events { get; private set; } = Array.Empty<IEvent>();

        public Task MaybeArchiveAsync<T>(TOperations operations, StreamCompactingRequest<T> request,
            IReadOnlyList<IEvent> events, CancellationToken cancellation) where T : class
        {
            Calls++;
            Events = events;

            return Task.CompletedTask;
        }
    }
}
