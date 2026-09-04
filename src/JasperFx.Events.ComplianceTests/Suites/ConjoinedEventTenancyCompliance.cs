using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Conjoined tenancy events and aggregate

public record ConsignmentBooked(string Destination);

public record ConsignmentScanned(string Location);

public record ConsignmentDelivered;

public partial class ComplianceConsignment
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty;
    public int ScanCount { get; set; }
    public bool Delivered { get; set; }

    public static ComplianceConsignment Create(ConsignmentBooked e) => new() { Destination = e.Destination };

    public void Apply(ConsignmentScanned _) => ScanCount++;

    public void Apply(ConsignmentDelivered _) => Delivered = true;
}

#endregion

/// <summary>
/// Conjoined event tenancy — one database, many tenants, with every stream and event scoped to a
/// tenant id.
/// </summary>
/// <remarks>
/// <para>
/// The property under test is <em>isolation</em>, and it is worth pinning across stores because the
/// failure mode is silent and asymmetric: a store that leaks across tenants still returns correct
/// answers for the tenant that happens to own the data, and only misbehaves for the other one. So
/// every test here checks both directions — what a tenant can see, and what it must not.
/// </para>
/// <para>
/// The suite deliberately reuses one stream id across two tenants. Under conjoined tenancy the
/// identity of a stream is (tenant, id), not id alone, and reusing the id is the sharpest way to
/// show it: a store that keys on id alone will either collide on append or return one tenant's
/// events to the other.
/// </para>
/// <para>
/// Cost is a single seam member, <see cref="ComplianceStoreConfig.ConjoinedEventTenancy"/>. Opening
/// a tenant-scoped session needs nothing new — <c>IEventStore&lt;TOperations,
/// TQuerySession&gt;.OpenSession(IEventDatabase, string)</c> is already shared and implemented by
/// both products, reached here by casting the fixture's non-generic <see cref="IEventStore"/> to
/// the closed generic, which is safe because this suite is generic over the same pair the store
/// closes over (marten#5148).
/// </para>
/// </remarks>
public abstract class ConjoinedEventTenancyCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_conjoined";
        config.ConjoinedEventTenancy = true;

        config.AddEventType<ConsignmentBooked>();
        config.AddEventType<ConsignmentScanned>();
        config.AddEventType<ConsignmentDelivered>();
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// A session bound to one tenant, through the shared generic store surface.
    /// </summary>
    private async Task<TOperations> openForTenantAsync(string tenantId)
    {
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;

        var databases = await theFixture.EventStore.AllDatabases();
        var database = databases.First();

        return store.OpenSession(database, tenantId);
    }

    private async Task appendAsync(string tenantId, Guid streamId, params object[] events)
    {
        await using var session = await openForTenantAsync(tenantId);
        EventsFor(session).StartStream<ComplianceConsignment>(streamId, events);
        await SaveChangesAsync(session);
    }

    [Fact]
    public async Task events_appended_for_one_tenant_are_not_visible_to_another()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));

        await using var query = await openForTenantAsync(TenantB);
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_tenant_sees_its_own_events()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));

        await using var query = await openForTenantAsync(TenantA);
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<ConsignmentBooked>();
        events[1].Data.ShouldBeOfType<ConsignmentScanned>();
    }

    /// <summary>
    /// Under conjoined tenancy a stream's identity is (tenant, id), not id alone.
    /// </summary>
    [Fact]
    public async Task the_same_stream_id_lives_independently_in_two_tenants()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));
        await appendAsync(TenantB, streamId, new ConsignmentBooked("Lisbon"));

        await using (var a = await openForTenantAsync(TenantA))
        {
            var events = await EventsFor(a).FetchStreamAsync(streamId, token: Cancellation);
            events.Count.ShouldBe(2);
            events[0].Data.ShouldBeOfType<ConsignmentBooked>().Destination.ShouldBe("Boston");
        }

        await using var b = await openForTenantAsync(TenantB);
        var others = await EventsFor(b).FetchStreamAsync(streamId, token: Cancellation);
        others.Count.ShouldBe(1);
        others[0].Data.ShouldBeOfType<ConsignmentBooked>().Destination.ShouldBe("Lisbon");
    }

    [Fact]
    public async Task stream_state_is_scoped_to_the_tenant()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"),
            new ConsignmentScanned("Hub"));
        await appendAsync(TenantB, streamId, new ConsignmentBooked("Lisbon"));

        await using (var a = await openForTenantAsync(TenantA))
        {
            var state = await EventsFor(a).FetchStreamStateAsync(streamId, Cancellation);
            state.ShouldNotBeNull();
            state.Version.ShouldBe(3);
        }

        await using var b = await openForTenantAsync(TenantB);
        var otherState = await EventsFor(b).FetchStreamStateAsync(streamId, Cancellation);
        otherState.ShouldNotBeNull();
        otherState.Version.ShouldBe(1);
    }

    [Fact]
    public async Task stream_state_is_null_for_a_tenant_that_has_no_such_stream()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"));

        await using var b = await openForTenantAsync(TenantB);
        var state = await EventsFor(b).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task every_event_is_stamped_with_its_tenant()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));

        await using var query = await openForTenantAsync(TenantA);
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.ShouldAllBe(x => x.TenantId == TenantA);
    }

    [Fact]
    public async Task live_aggregation_is_scoped_to_the_tenant()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"),
            new ConsignmentScanned("Hub"), new ConsignmentDelivered());
        await appendAsync(TenantB, streamId, new ConsignmentBooked("Lisbon"));

        await using (var a = await openForTenantAsync(TenantA))
        {
            var consignment = await EventsFor(a).AggregateStreamAsync<ComplianceConsignment>(streamId, token: Cancellation);
            consignment.ShouldNotBeNull();
            consignment.Destination.ShouldBe("Boston");
            consignment.ScanCount.ShouldBe(2);
            consignment.Delivered.ShouldBeTrue();
        }

        await using var b = await openForTenantAsync(TenantB);
        var other = await EventsFor(b).AggregateStreamAsync<ComplianceConsignment>(streamId, token: Cancellation);
        other.ShouldNotBeNull();
        other.Destination.ShouldBe("Lisbon");
        other.ScanCount.ShouldBe(0);
        other.Delivered.ShouldBeFalse();
    }

    [Fact]
    public async Task appending_to_one_tenant_does_not_move_another_tenants_version()
    {
        var streamId = Guid.NewGuid();

        await appendAsync(TenantA, streamId, new ConsignmentBooked("Boston"));
        await appendAsync(TenantB, streamId, new ConsignmentBooked("Lisbon"));

        await using (var a = await openForTenantAsync(TenantA))
        {
            EventsFor(a).Append(streamId, new ConsignmentScanned("Depot"), new ConsignmentScanned("Hub"));
            await SaveChangesAsync(a);
        }

        await using var b = await openForTenantAsync(TenantB);
        var state = await EventsFor(b).FetchStreamStateAsync(streamId, Cancellation);

        state.ShouldNotBeNull();
        state.Version.ShouldBe(1);
    }

    /// <summary>
    /// The <see cref="EventQuery.TenantId"/> filter on the cross-stream query surface. Lives here
    /// rather than in <see cref="EventQueryCompliance{TFixture,TOperations,TQuerySession}"/> because
    /// this suite owns the conjoined-tenancy store configuration — the same division of labor as
    /// the explorer suite's per-tenant overloads. See jasperfx#737.
    /// </summary>
    [Fact]
    public async Task query_events_filtered_by_tenant_id()
    {
        await appendAsync(TenantA, Guid.NewGuid(), new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));
        await appendAsync(TenantB, Guid.NewGuid(), new ConsignmentBooked("Lisbon"));

        var readOnly = theFixture.EventStore.OpenReadOnlyEventStore();

        var result = await readOnly.QueryEventsAsync(
            new EventQuery { TenantId = TenantA, PageSize = 1000 }, Cancellation);

        // Both directions, as everywhere in this suite: tenant A's events are all there, and
        // tenant B's event is not — a leak still returns correct answers for the tenant that
        // happens to own the data.
        result.TotalCount.ShouldBe(2);
        result.Events.Count.ShouldBe(2);
        result.Events.ShouldAllBe(x => x.TenantId == TenantA);

        var other = await readOnly.QueryEventsAsync(
            new EventQuery { TenantId = TenantB, PageSize = 1000 }, Cancellation);

        other.TotalCount.ShouldBe(1);
        other.Events.Single().TenantId.ShouldBe(TenantB);
        other.Events.Single().Data.ShouldBeOfType<ConsignmentBooked>().Destination.ShouldBe("Lisbon");
    }

    /// <summary>
    /// The tenant filter AND-composes with the other <see cref="EventQuery"/> filters rather than
    /// replacing them. Decoys in both directions: the matching tenant holds a non-matching event
    /// type, and the other tenant holds a matching one — so dropping either filter changes the
    /// answer. (Deliberately a type filter rather than a tag condition: tag types are not part of
    /// this suite's store configuration, and tags-under-conjoined-tenancy is DCB-suite territory.)
    /// </summary>
    [Fact]
    public async Task query_events_composes_the_tenant_filter_with_other_filters()
    {
        await appendAsync(TenantA, Guid.NewGuid(), new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));
        await appendAsync(TenantA, Guid.NewGuid(), new ConsignmentBooked("Chicago"));
        await appendAsync(TenantB, Guid.NewGuid(), new ConsignmentBooked("Lisbon"));

        var result = await theFixture.EventStore.OpenReadOnlyEventStore().QueryEventsAsync(
            new EventQuery
            {
                TenantId = TenantA, EventTypeName = EventTypeNameFor<ConsignmentBooked>(), PageSize = 1000
            },
            Cancellation);

        result.TotalCount.ShouldBe(2);
        result.Events.ShouldAllBe(x => x.TenantId == TenantA);
        result.Events.Select(x => ((ConsignmentBooked)x.Data!).Destination)
            .OrderBy(x => x).ShouldBe(["Boston", "Chicago"]);
    }
}
