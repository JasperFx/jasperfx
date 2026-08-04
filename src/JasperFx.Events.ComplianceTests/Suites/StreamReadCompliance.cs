using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Stream read events

public record ExpeditionPlanned(string Destination);

public record SuppliesLoaded(int Crates);

public record CampMade(string Place);

public record ExpeditionFinished;

#endregion

/// <summary>
/// The read side of the event store — <c>FetchStreamAsync</c> with its version, <c>fromVersion</c>
/// and timestamp bounds, <c>FetchStreamStateAsync</c>, and single-event <c>LoadAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is on <see cref="IQueryEventStore"/>, so no fixture member is needed. The
/// aggregate-folding overloads (<c>AggregateStreamAsync</c>) are deliberately left to the live
/// aggregation suite; this one is about the raw event and stream metadata a store hands back.
/// </para>
/// <para>
/// The time-travel test derives its cut-off from the *stored* event timestamps rather than the test
/// host's clock — the two live on different machines in CI, and a store is free to stamp events
/// server-side. It also picks a point strictly between two events, so the suite does not
/// accidentally pin whether a store's timestamp filter is inclusive or exclusive.
/// </para>
/// </remarks>
public abstract class StreamReadCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_stream_reads";
        config.AddEventType<ExpeditionPlanned>();
        config.AddEventType<SuppliesLoaded>();
        config.AddEventType<CampMade>();
        config.AddEventType<ExpeditionFinished>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<Guid> aStreamOfFourAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId,
            new ExpeditionPlanned("The North"),
            new SuppliesLoaded(12),
            new CampMade("Base Camp"),
            new ExpeditionFinished());
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task fetch_stream_returns_every_event_in_order()
    {
        var streamId = await aStreamOfFourAsync();

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);

        events.Count.ShouldBe(4);
        events.Select(x => x.Version).ShouldBe(new long[] { 1, 2, 3, 4 });
        events.Select(x => x.Data.GetType()).ShouldBe(new[]
        {
            typeof(ExpeditionPlanned), typeof(SuppliesLoaded), typeof(CampMade), typeof(ExpeditionFinished)
        });

        foreach (var @event in events)
        {
            @event.StreamId.ShouldBe(streamId);
        }

        // Sequence is store-global and monotonic, unlike the per-stream Version.
        events.Select(x => x.Sequence).ShouldBeInOrder();
        events[0].EventTypeName.ShouldBe(EventTypeNameFor<ExpeditionPlanned>());
    }

    [Fact]
    public async Task fetch_stream_bounded_by_version()
    {
        var streamId = await aStreamOfFourAsync();

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, 2, token: Cancellation);

        events.Count.ShouldBe(2);
        events.Select(x => x.Version).ShouldBe(new long[] { 1, 2 });
    }

    [Fact]
    public async Task fetch_stream_from_a_starting_version()
    {
        var streamId = await aStreamOfFourAsync();

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, fromVersion: 3, token: Cancellation);

        events.Select(x => x.Version).ShouldBe(new long[] { 3, 4 });
    }

    [Fact]
    public async Task fetch_stream_within_a_version_window()
    {
        var streamId = await aStreamOfFourAsync();

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, 3, fromVersion: 2, token: Cancellation);

        events.Select(x => x.Version).ShouldBe(new long[] { 2, 3 });
    }

    [Fact]
    public async Task fetch_stream_as_of_a_timestamp()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new ExpeditionPlanned("The North"), new SuppliesLoaded(12));
        await SaveChangesAsync(session);

        // A real gap so the two commits cannot land on the same server tick.
        await Task.Delay(50, Cancellation);

        EventsFor(session).Append(streamId, new CampMade("Base Camp"), new ExpeditionFinished());
        await SaveChangesAsync(session);

        var all = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
        all.Count.ShouldBe(4);

        var lastOfFirstCommit = all[1].Timestamp;
        var firstOfSecondCommit = all[2].Timestamp;
        firstOfSecondCommit.ShouldBeGreaterThan(lastOfFirstCommit);

        // Strictly between the two commits, so inclusive-vs-exclusive filtering is not pinned here.
        var cutoff = lastOfFirstCommit + (firstOfSecondCommit - lastOfFirstCommit) / 2;

        var asOf = await EventsFor(session).FetchStreamAsync(streamId, timestamp: cutoff, token: Cancellation);

        asOf.Select(x => x.Version).ShouldBe(new long[] { 1, 2 });
    }

    [Fact]
    public async Task fetch_stream_for_an_unknown_stream_is_empty()
    {
        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(Guid.NewGuid(), token: Cancellation);

        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task fetch_stream_state_reports_version_and_timestamps()
    {
        var streamId = await aStreamOfFourAsync();

        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.Id.ShouldBe(streamId);
        state.Version.ShouldBe(4);
        state.Created.ShouldNotBe(default);
        state.LastTimestamp.ShouldNotBe(default);
        state.LastTimestamp.ShouldBeGreaterThanOrEqualTo(state.Created);
    }

    [Fact]
    public async Task fetch_stream_state_for_an_unknown_stream_is_null()
    {
        await using var session = OpenSession();
        var state = await EventsFor(session).FetchStreamStateAsync(Guid.NewGuid(), Cancellation);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task stream_state_carries_the_aggregate_type_when_the_stream_was_typed()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceAccount>(streamId, new AccountOpened("Hilda"));
        await SaveChangesAsync(session);

        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.AggregateType.ShouldBe(typeof(ComplianceAccount));
    }

    [Fact]
    public async Task load_a_single_event_typed_and_untyped()
    {
        var streamId = await aStreamOfFourAsync();

        await using var session = OpenSession();
        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
        var second = events[1];

        var typed = await EventsFor(session).LoadAsync<SuppliesLoaded>(second.Id, Cancellation);
        typed.ShouldNotBeNull();
        typed.Data.Crates.ShouldBe(12);
        typed.Version.ShouldBe(2);
        typed.StreamId.ShouldBe(streamId);

        var untyped = await EventsFor(session).LoadAsync(second.Id, Cancellation);
        untyped.ShouldNotBeNull();
        untyped.Id.ShouldBe(second.Id);
        untyped.Data.ShouldBeOfType<SuppliesLoaded>();
    }

    [Fact]
    public async Task load_an_unknown_event_returns_null()
    {
        await using var session = OpenSession();

        var untyped = await EventsFor(session).LoadAsync(Guid.NewGuid(), Cancellation);
        untyped.ShouldBeNull();
    }
}
