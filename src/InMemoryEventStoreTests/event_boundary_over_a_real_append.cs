using JasperFx.Events;
using JasperFx.Events.InMemory;
using JasperFx.Events.Tags;
using Shouldly;

namespace InMemoryEventStoreTests;

public record DepotId(Guid Value);

public record RouteName(string Value);

public record ParcelScanned(DepotId Depot, string Barcode);

public record ParcelWeighed(string Barcode);

public class DepotActivity;

/// <summary>
/// <see cref="EventBoundary{T}"/> (jasperfx#1044) over real <c>Append</c> and <c>StartStream</c>
/// implementations rather than a fake of them. The in-memory store refuses DCB queries, so nothing here
/// reaches <c>FetchForWritingByTags</c>; this is only the write half, which is the half the type owns,
/// read back through the store. Unit coverage of the routing is in EventTests.
/// </summary>
public class event_boundary_over_a_real_append
{
    private const string TheTenant = "tenant-b";

    private static readonly ITagTypeRegistration[] TagTypes =
    [
        TagTypeRegistration.Create<DepotId>("depot").ForAggregate<DepotActivity>(),
        TagTypeRegistration.Create<RouteName>("route")
    ];

    private readonly InMemoryDocumentStore theStore = new();

    private EventBoundary<DepotActivity> BoundaryFor(InMemoryDocumentSession session)
        => new(session.Events, theStore.Events, TagTypes, null, []);

    [Fact]
    public async Task an_event_routed_by_tag_is_stored_in_the_stream_the_tag_names()
    {
        var depot = Guid.NewGuid();

        await using (var session = theStore.LightweightSession(TheTenant))
        {
            var boundary = BoundaryFor(session);
            boundary.AppendOne(new ParcelScanned(new DepotId(depot), "A1"));
            boundary.AppendOne(new ParcelScanned(new DepotId(depot), "A2"));

            await session.SaveChangesAsync();
        }

        await using var reader = theStore.LightweightSession(TheTenant);
        var events = await reader.Events.FetchStreamAsync(depot);

        events.Select(x => ((ParcelScanned)x.Data).Barcode).ShouldBe(["A1", "A2"]);
        events.Select(x => x.Version).ShouldBe([1L, 2L]);
        events.ShouldAllBe(x => x.TenantId == TheTenant);
    }

    [Fact]
    public async Task appending_again_extends_the_stream_rather_than_colliding_with_it()
    {
        var depot = Guid.NewGuid();

        foreach (var barcode in new[] { "A1", "A2" })
        {
            await using var session = theStore.LightweightSession();
            BoundaryFor(session).AppendOne(new ParcelScanned(new DepotId(depot), barcode));
            await session.SaveChangesAsync();
        }

        await using var reader = theStore.LightweightSession();
        (await reader.Events.FetchStreamStateAsync(depot))!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task a_wrapped_event_keeps_what_the_caller_put_on_it()
    {
        var depot = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await using (var session = theStore.LightweightSession())
        {
            var wrapped = session.Events.BuildEvent(new ParcelWeighed("A1")).WithTag(new DepotId(depot));
            wrapped.Id = eventId;

            BoundaryFor(session).AppendOne(wrapped);
            await session.SaveChangesAsync();
        }

        await using var reader = theStore.LightweightSession();
        var stored = (await reader.Events.FetchStreamAsync(depot)).ShouldHaveSingleItem();

        stored.Id.ShouldBe(eventId);
        stored.Data.ShouldBeOfType<ParcelWeighed>();
    }

    [Fact]
    public async Task an_unrouted_event_starts_a_stream_of_its_own_in_the_same_tenant()
    {
        await using var session = theStore.LightweightSession(TheTenant);

        BoundaryFor(session).AppendOne(session.Events.BuildEvent(new ParcelWeighed("A1")).WithTag(new RouteName("north")));

        var stream = session.PendingStreams.ShouldHaveSingleItem();
        stream.ActionType.ShouldBe(StreamActionType.Start);
        stream.TenantId.ShouldBe(TheTenant);

        await session.SaveChangesAsync();

        await using var reader = theStore.LightweightSession(TheTenant);
        (await reader.Events.FetchStreamAsync(stream.Id)).ShouldHaveSingleItem().TenantId.ShouldBe(TheTenant);
    }

    [Fact]
    public async Task routes_by_string_key_under_string_identity()
    {
        theStore.Events.StreamIdentity = StreamIdentity.AsString;
        var depot = Guid.Parse("0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9");

        await using (var session = theStore.LightweightSession())
        {
            BoundaryFor(session).AppendOne(new ParcelScanned(new DepotId(depot), "A1"));
            await session.SaveChangesAsync();
        }

        await using var reader = theStore.LightweightSession();
        (await reader.Events.FetchStreamAsync("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task an_unrouted_event_starts_a_keyed_stream_under_string_identity()
    {
        theStore.Events.StreamIdentity = StreamIdentity.AsString;

        await using var session = theStore.LightweightSession();

        BoundaryFor(session).AppendOne(session.Events.BuildEvent(new ParcelWeighed("A1")).WithTag(new RouteName("north")));

        var stream = session.PendingStreams.ShouldHaveSingleItem();
        stream.Key.ShouldNotBeNullOrEmpty();

        await session.SaveChangesAsync();

        await using var reader = theStore.LightweightSession();
        (await reader.Events.FetchStreamAsync(stream.Key!)).ShouldHaveSingleItem();
    }
}
