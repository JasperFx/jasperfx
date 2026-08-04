using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Metadata events

public record ShipmentBooked(string Reference);

public record ShipmentDispatched(DateTimeOffset When);

#endregion

/// <summary>
/// The <see cref="IEvent"/> contract itself — the envelope every store hands back around a user's
/// event body: identity, per-stream version, store-global sequence, server timestamp, type naming,
/// tenant, and the header dictionary.
/// </summary>
/// <remarks>
/// <para>
/// This is the surface most likely to drift quietly between implementations, because none of it is
/// what a test is usually looking at — it is what a test takes for granted. Two independently
/// written event stores can easily disagree on whether <c>Sequence</c> is per-stream or global, or
/// whether <c>Timestamp</c> comes from the client or the server, and nothing fails until someone
/// builds tooling on top.
/// </para>
/// <para>
/// Headers need <see cref="ComplianceStoreConfig.EnableHeaders"/> (off by default in both products),
/// but stamping them needs no fixture member — <see cref="IEvent.SetHeader"/> is shared, so the
/// suite builds an envelope with <c>BuildEvent</c> and appends it.
/// </para>
/// </remarks>
public abstract class EventMetadataCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_event_metadata";
        config.EnableHeaders = true;
        config.AddEventType<ShipmentBooked>();
        config.AddEventType<ShipmentDispatched>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    [Fact]
    public async Task the_envelope_is_fully_populated_after_a_round_trip()
    {
        var streamId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new ShipmentBooked("SHP-1"));
        await SaveChangesAsync(session);

        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);
        var @event = events.Single();

        @event.Id.ShouldNotBe(Guid.Empty);
        @event.StreamId.ShouldBe(streamId);
        @event.Version.ShouldBe(1);
        @event.Sequence.ShouldBeGreaterThan(0);
        @event.Data.ShouldBeOfType<ShipmentBooked>();
        @event.EventType.ShouldBe(typeof(ShipmentBooked));
        @event.EventTypeName.ShouldBe(EventTypeNameFor<ShipmentBooked>());
        @event.DotNetTypeName.ShouldNotBeNullOrEmpty();
        @event.IsArchived.ShouldBeFalse();

        // Server-assigned, so all the suite can assert portably is that it is real and recent.
        @event.Timestamp.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task version_is_per_stream_and_one_based()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(first, new ShipmentBooked("A-1"), new ShipmentBooked("A-2"));
        EventsFor(session).StartStream(second, new ShipmentBooked("B-1"));
        await SaveChangesAsync(session);

        var firstEvents = await EventsFor(session).FetchStreamAsync(first, token: Cancellation);
        var secondEvents = await EventsFor(session).FetchStreamAsync(second, token: Cancellation);

        firstEvents.Select(x => x.Version).ShouldBe(new long[] { 1, 2 });

        // Restarts at 1 for a different stream -- Version is not the global counter.
        secondEvents.Single().Version.ShouldBe(1);
    }

    [Fact]
    public async Task sequence_is_store_global_and_monotonic_across_streams()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(first, new ShipmentBooked("A-1"));
        await SaveChangesAsync(session);

        EventsFor(session).StartStream(second, new ShipmentBooked("B-1"));
        await SaveChangesAsync(session);

        var firstEvents = await EventsFor(session).FetchStreamAsync(first, token: Cancellation);
        var secondEvents = await EventsFor(session).FetchStreamAsync(second, token: Cancellation);

        secondEvents.Single().Sequence.ShouldBeGreaterThan(firstEvents.Single().Sequence);
    }

    [Fact]
    public async Task timestamps_advance_with_the_stream()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new ShipmentBooked("SHP-1"));
        await SaveChangesAsync(session);

        await Task.Delay(50, Cancellation);

        EventsFor(session).Append(streamId, new ShipmentDispatched(DateTimeOffset.UtcNow));
        await SaveChangesAsync(session);

        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);

        events[1].Timestamp.ShouldBeGreaterThan(events[0].Timestamp);

        // Ordering by timestamp agrees with ordering by sequence within one stream.
        events.OrderBy(x => x.Timestamp).Select(x => x.Sequence)
            .ShouldBe(events.OrderBy(x => x.Sequence).Select(x => x.Sequence));
    }

    [Fact]
    public async Task event_type_name_matches_the_registry()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new ShipmentBooked("SHP-1"), new ShipmentDispatched(DateTimeOffset.UtcNow));
        await SaveChangesAsync(session);

        var events = await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation);

        // Asserted through the shared registry surface rather than any store-internal generic, so
        // this stays a zero-InternalsVisibleTo suite.
        events[0].EventTypeName.ShouldBe(EventTypeNameFor<ShipmentBooked>());
        events[1].EventTypeName.ShouldBe(EventTypeNameFor<ShipmentDispatched>());
        events[0].EventTypeName.ShouldNotBe(events[1].EventTypeName);
    }

    [Fact]
    public async Task headers_survive_the_round_trip()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var events = EventsFor(session);

        var wrapped = events.BuildEvent(new ShipmentBooked("SHP-1"));
        wrapped.SetHeader("origin", "compliance");
        wrapped.SetHeader("attempt", 3);
        events.StartStream(streamId, wrapped);
        await SaveChangesAsync(session);

        var stored = (await events.FetchStreamAsync(streamId, token: Cancellation)).Single();

        stored.GetHeader("origin").ShouldNotBeNull();
        stored.GetHeader("origin").ToString().ShouldBe("compliance");
        stored.GetHeader("attempt").ShouldNotBeNull();
        stored.GetHeader("attempt").ToString().ShouldBe("3");
    }

    [Fact]
    public async Task an_event_without_headers_reports_no_header_rather_than_throwing()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new ShipmentBooked("SHP-1"));
        await SaveChangesAsync(session);

        var stored = (await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation)).Single();

        stored.GetHeader("nope").ShouldBeNull();
    }

    [Fact]
    public async Task events_carry_the_default_tenant_in_a_single_tenant_store()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new ShipmentBooked("SHP-1"));
        await SaveChangesAsync(session);

        var stored = (await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation)).Single();

        stored.TenantId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task the_typed_event_wrapper_exposes_the_same_metadata()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new ShipmentBooked("SHP-1"));
        await SaveChangesAsync(session);

        var stored = (await EventsFor(session).FetchStreamAsync(streamId, token: Cancellation)).Single();
        var typed = await EventsFor(session).LoadAsync<ShipmentBooked>(stored.Id, Cancellation);

        typed.ShouldNotBeNull();
        typed.Id.ShouldBe(stored.Id);
        typed.Version.ShouldBe(stored.Version);
        typed.Sequence.ShouldBe(stored.Sequence);
        typed.StreamId.ShouldBe(stored.StreamId);
        typed.Data.Reference.ShouldBe("SHP-1");
    }
}
