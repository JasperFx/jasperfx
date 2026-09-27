using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
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

/// <summary>
/// A second aggregate over the same events, snapshotted <em>asynchronously</em> where
/// <see cref="ComplianceConsignment" /> is snapshotted inline.
/// </summary>
/// <remarks>
/// <para>
/// A separate type rather than a second registration, because a type can carry only one snapshot
/// lifecycle — and the two lifecycles have to be covered separately here. The inline path writes the
/// document inside the appending session, where the tenant is whatever that session was opened for;
/// the async path writes it from a daemon shard, where the tenant has to survive being read off the
/// event, grouped, and handed to a projection batch. Those are different pieces of code on every
/// store, and only the second one has to reconstruct a tenant it was not told.
/// </para>
/// <para>
/// It deliberately does not apply <see cref="ConsignmentDelivered" />, so the two snapshots of one
/// stream are distinguishable in a read-back.
/// </para>
/// </remarks>
public partial class ComplianceConsignmentLedger
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty;
    public int ScanCount { get; set; }

    public static ComplianceConsignmentLedger Create(ConsignmentBooked e) => new() { Destination = e.Destination };

    public void Apply(ConsignmentScanned _) => ScanCount++;
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

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many tenants the rebuild fact spreads one stream id across. Large on purpose: a grouping
    /// bug that folds tenants together is invisible at two, and the daemon's own paging and batching
    /// only come into play at a few hundred slices.
    /// </summary>
    private const int RebuildTenantCount = 200;

    /// <summary>
    /// One outbox instance for the whole suite, for the same reason
    /// <see cref="ProjectionSideEffectCompliance{TFixture,TOperations,TQuerySession}" /> holds one:
    /// the configuration delegate is what the fixture keys a store rebuild on, so a fresh instance
    /// per fact would need a fresh delegate. The fact that uses it resets it before acting.
    /// </summary>
    private static readonly RecordingMessageOutbox _outbox = new();

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_conjoined";
        config.ConjoinedEventTenancy = true;

        config.AddEventType<ConsignmentBooked>();
        config.AddEventType<ConsignmentScanned>();
        config.AddEventType<ConsignmentDelivered>();

        // jasperfx#898: until these two registrations existed, NO fact anywhere in the library
        // registered a projection and then wrote the same stream id in two tenants -- so a store that
        // folded two tenants' streams into one single-tenanted snapshot row passed the whole suite.
        // Both lifecycles, because the inline write and the daemon's write are different code paths
        // and only the second has to reconstruct a tenant it was not handed. Registered in the
        // STANDARD configuration rather than behind a second delegate so that every other fact in the
        // suite exercises the inline snapshot path as well.
        config.Snapshot<ComplianceConsignment>(SnapshotLifecycle.Inline);
        config.Snapshot<ComplianceConsignmentLedger>(SnapshotLifecycle.Async);
    };

    /// <summary>
    /// The same store with string stream identity, for the second arm of the archiving fact. Its own
    /// schema, necessarily: stream identity decides the type of the stream id column, so the two
    /// configurations cannot share tables.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _stringIdentityConfiguration = config =>
    {
        config.SchemaName = "compliance_conjoined_string";
        config.ConjoinedEventTenancy = true;
        config.StreamIdentity = StreamIdentity.AsString;

        config.AddEventType<ConsignmentBooked>();
        config.AddEventType<ConsignmentScanned>();
        config.AddEventType<ConsignmentDelivered>();
    };

    /// <summary>
    /// A store that refuses the default tenant. Its own delegate, and it has to be: most facts in
    /// this suite open a tenant-less session somewhere, and this store refuses one.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _defaultTenantDisabledConfiguration = config =>
    {
        config.SchemaName = "compliance_conjoined_no_default";
        config.ConjoinedEventTenancy = true;
        config.DisableDefaultTenantUsage = true;

        config.AddEventType<ConsignmentBooked>();
        config.AddEventType<ConsignmentScanned>();
        config.AddEventType<ConsignmentDelivered>();
    };

    /// <summary>
    /// A store with the recording outbox installed, for the side-effect tenant fact. Its own
    /// delegate for the reason
    /// <see cref="ProjectionSideEffectCompliance{TFixture,TOperations,TQuerySession}" /> gives:
    /// installing the outbox drives store <em>construction</em> through a registrar member with a
    /// throwing default, so a store without one must never reach it.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _outboxConfiguration = config =>
    {
        config.SchemaName = "compliance_conjoined_outbox";
        config.ConjoinedEventTenancy = true;

        config.AddEventType<WatchtowerManned>();
        config.AddEventType<WatchtowerRelieved>();
        config.AddEventType<WatchtowerAudited>();

        config.AddProjection(new ComplianceWatchtowerProjection(), ProjectionLifecycle.Async);
        config.UseMessageOutbox(_outbox);
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

    /// <summary>
    /// <c>QueryStreamStates(tenantId)</c> scopes the streams table to one tenant (jasperfx#740).
    /// Lives here rather than in <c>StreamStateQueryCompliance</c> because this suite owns the
    /// conjoined-tenancy store configuration — the same division of labor as the event-query and
    /// explorer tenant coverage. Reuses one stream id across both tenants, this suite's sharpest
    /// trick: under conjoined tenancy the identity of a stream is (tenant, id), so a store keying
    /// on id alone returns one tenant's stream metadata to the other.
    /// </summary>
    [Fact]
    public async Task query_stream_states_scoped_to_a_tenant()
    {
        var shared = Guid.NewGuid();
        var onlyA = Guid.NewGuid();

        await appendAsync(TenantA, shared, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));
        await appendAsync(TenantA, onlyA, new ConsignmentBooked("Chicago"));
        await appendAsync(TenantB, shared, new ConsignmentBooked("Lisbon"));

        var readOnly = theFixture.EventStore.OpenReadOnlyEventStore();

        var forA = await Documents.DocumentQueryableExtensions.ToListAsync(
            readOnly.QueryStreamStates(TenantA), Cancellation);

        // Both directions, as everywhere in this suite: tenant A's two streams are there, tenant
        // B's copy of the shared id is not — its version would bleed through as a wrong Version on
        // the shared stream, so assert the version too.
        forA.Count.ShouldBe(2);
        forA.Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { shared, onlyA }.OrderBy(x => x));
        forA.Single(x => x.Id == shared).Version.ShouldBe(2);

        var forB = await Documents.DocumentQueryableExtensions.ToListAsync(
            readOnly.QueryStreamStates(TenantB), Cancellation);

        var bStream = forB.ShouldHaveSingleItem();
        bStream.Id.ShouldBe(shared);
        bStream.Version.ShouldBe(1);
    }

    /// <summary>
    /// <c>OpenReadOnlyEventStore(tenantId)</c> opens the read-only tier <em>in</em> one tenant's
    /// scope (jasperfx#885), which is what makes the tier's tenant-less members — everything except
    /// <c>QueryStreamStates</c> and <see cref="EventQuery.TenantId"/> — usable under tenancy at all,
    /// and what makes the whole surface reachable on a store whose default tenant is disabled.
    /// Asserted through the shared stream id again: the same <c>FetchStreamStateAsync</c> call must
    /// answer differently depending only on which tenant the reader was opened for.
    /// </summary>
    [Fact]
    public async Task read_only_event_store_opened_for_a_tenant_scopes_the_tenant_less_reads()
    {
        var shared = Guid.NewGuid();

        await appendAsync(TenantA, shared, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));
        await appendAsync(TenantB, shared, new ConsignmentBooked("Lisbon"));

        IReadOnlyEventStore forA;
        try
        {
            forA = theFixture.EventStore.OpenReadOnlyEventStore(TenantA);
        }
        catch (NotSupportedException)
        {
            Assert.Skip("This store does not implement the tenant-aware OpenReadOnlyEventStore(tenantId) overload.");
            return;
        }

        var stateForA = await forA.FetchStreamStateAsync(shared, Cancellation);
        stateForA.ShouldNotBeNull();
        stateForA.Version.ShouldBe(2);

        var eventsForA = await forA.FetchStreamAsync(shared, token: Cancellation);
        eventsForA.Count.ShouldBe(2);

        // Both directions, as everywhere in this suite: tenant B's reader must see only its own
        // single event on the same stream id, not tenant A's two.
        var forB = theFixture.EventStore.OpenReadOnlyEventStore(TenantB);

        var stateForB = await forB.FetchStreamStateAsync(shared, Cancellation);
        stateForB.ShouldNotBeNull();
        stateForB.Version.ShouldBe(1);

        var eventsForB = await forB.FetchStreamAsync(shared, token: Cancellation);
        eventsForB.ShouldHaveSingleItem().Data.ShouldBeOfType<ConsignmentBooked>().Destination.ShouldBe("Lisbon");
    }

    private void SkipUnlessDaemonIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test.");
    }

    /// <summary>
    /// Poll one tenant's copy of a stream until it reaches a version.
    /// </summary>
    /// <remarks>
    /// The tenant-scoped twin of <c>ProjectionSideEffectCompliance</c>'s wait, and needed for the same
    /// reason: a projection that raises an event moves the stream it just folded, so the shard is
    /// stale again the instant it writes one and "non-stale" is true in a window before the side
    /// effect exists. Per tenant because the two copies of the shared stream id progress
    /// independently — which is the property under test, so the wait must not assume it.
    /// </remarks>
    private async Task waitForStreamVersionAsync(string tenantId, Guid streamId, long version)
    {
        var deadline = DateTimeOffset.UtcNow.Add(_timeout);
        long actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using (var session = await openForTenantAsync(tenantId))
            {
                var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
                actual = state?.Version ?? 0;
            }

            if (actual >= version) return;

            await Task.Delay(100, Cancellation);
        }

        throw new TimeoutException(
            $"Timed out after {_timeout} waiting for stream {streamId} in tenant '{tenantId}' to reach version {version}; it is at {actual}.");
    }

    /// <summary>
    /// A projected snapshot of one stream id is one document <em>per tenant</em>, on both the inline
    /// and the async write path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fact jasperfx#898 was opened for, and the one most likely to go red.</b> Every tenanted
    /// fact in this library before it used distinct stream ids per tenant, so a store that projected
    /// into a single-tenanted document table — one row keyed on the stream id alone — passed all of
    /// them. What that store actually does is let the second tenant's projection <em>overwrite</em> the
    /// first tenant's snapshot, silently, with no error at append time and no error at read time:
    /// both tenants then read a document describing the other one's stream.
    /// </para>
    /// <para>
    /// Both lifecycles in one fact because they fail independently. The inline write happens inside
    /// the appending session, which already knows its tenant, so a store gets that one right almost by
    /// accident; the async write happens on a daemon shard, where the tenant has to be read off the
    /// event, carried through slicing, and applied to the document write. A store can have the first
    /// and not the second, and the second is the one a production deployment relies on.
    /// </para>
    /// <para>
    /// The assertions are on the document <em>contents</em>, not merely on its presence. A store that
    /// keeps one row per id answers both loads non-null — with the same document — so
    /// <c>ShouldNotBeNull</c> alone would pass on exactly the store this fact exists to catch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task inline_and_async_snapshots_of_a_shared_stream_id_stay_isolated_per_tenant()
    {
        SkipUnlessDaemonIsSupported();

        var shared = Guid.NewGuid();

        await appendAsync(TenantA, shared, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"),
            new ConsignmentScanned("Hub"));
        await appendAsync(TenantB, shared, new ConsignmentBooked("Lisbon"));

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using (var a = await openForTenantAsync(TenantA))
        {
            var inline = (await LoadDocumentAsync<ComplianceConsignment>(a, shared)).ShouldNotBeNull();
            inline.Destination.ShouldBe("Boston");
            inline.ScanCount.ShouldBe(2);

            var async = (await LoadDocumentAsync<ComplianceConsignmentLedger>(a, shared)).ShouldNotBeNull();
            async.Destination.ShouldBe("Boston");
            async.ScanCount.ShouldBe(2);
        }

        // Both directions, as everywhere in this suite. Tenant B's snapshots describe tenant B's
        // stream -- an overwriting store answers "Boston" here, for both lifecycles.
        await using var b = await openForTenantAsync(TenantB);

        var otherInline = (await LoadDocumentAsync<ComplianceConsignment>(b, shared)).ShouldNotBeNull();
        otherInline.Destination.ShouldBe("Lisbon");
        otherInline.ScanCount.ShouldBe(0);

        var otherAsync = (await LoadDocumentAsync<ComplianceConsignmentLedger>(b, shared)).ShouldNotBeNull();
        otherAsync.Destination.ShouldBe("Lisbon");
        otherAsync.ScanCount.ShouldBe(0);
    }

    /// <summary>
    /// Archiving a stream in one tenant leaves the same stream id live in another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StreamArchivingCompliance</c> has no tenant facts and this suite had no archiving fact, so
    /// the intersection was uncovered — and it is the intersection where the bug lives. Archiving is
    /// a bulk update over a stream's rows plus a flag on the stream itself, which is exactly the
    /// shape of operation whose WHERE clause a store forgets to scope: an unscoped archive takes
    /// every tenant's copy of the id with it, and the only tenant who notices is the one who did not
    /// ask.
    /// </para>
    /// <para>
    /// The assertions run in both directions and reach past the flag. Tenant B's stream must still
    /// report <c>IsArchived == false</c>, its events must still read back <em>un</em>-archived, and —
    /// the load-bearing one — it must still be <b>appendable</b>. A store that flipped the flag
    /// everywhere but left the rows alone passes the first two and refuses the third.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task archiving_a_stream_in_one_tenant_leaves_the_same_stream_id_live_in_another()
    {
        var shared = Guid.NewGuid();

        await appendAsync(TenantA, shared, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));
        await appendAsync(TenantB, shared, new ConsignmentBooked("Lisbon"));

        await using (var a = await openForTenantAsync(TenantA))
        {
            EventsFor(a).ArchiveStream(shared);
            await SaveChangesAsync(a);
        }

        await using (var a = await openForTenantAsync(TenantA))
        {
            var mine = await EventsFor(a).FetchStreamStateAsync(shared, Cancellation);
            mine.ShouldNotBeNull();
            mine.IsArchived.ShouldBeTrue();
        }

        await using (var b = await openForTenantAsync(TenantB))
        {
            var theirs = await EventsFor(b).FetchStreamStateAsync(shared, Cancellation);
            theirs.ShouldNotBeNull();
            theirs.IsArchived.ShouldBeFalse();
            theirs.Version.ShouldBe(1);

            var events = await EventsFor(b).FetchStreamAsync(shared, token: Cancellation);
            events.ShouldHaveSingleItem().IsArchived.ShouldBeFalse();
        }

        // Still appendable, which is the half a flag-only leak survives.
        await using (var b = await openForTenantAsync(TenantB))
        {
            EventsFor(b).Append(shared, new ConsignmentScanned("Porto"));
            await SaveChangesAsync(b);
        }

        await using var check = await openForTenantAsync(TenantB);
        var after = await EventsFor(check).FetchStreamStateAsync(shared, Cancellation);
        after.ShouldNotBeNull();
        after.IsArchived.ShouldBeFalse();
        after.Version.ShouldBe(2);
    }

    /// <summary>
    /// The same claim under <b>string</b> stream identity.
    /// </summary>
    /// <remarks>
    /// A second arm rather than a parameter, because stream identity is store <em>configuration</em> —
    /// it decides the type of the stream id column — so it takes its own store and its own schema.
    /// Worth the rebuild: every fact in this suite was Guid-only before jasperfx#898, and a string
    /// stream key is a different composite key on every store. A store that scoped
    /// <c>(tenant_id, id)</c> correctly and <c>(tenant_id, key)</c> not at all would have shown
    /// nothing here.
    /// </remarks>
    [Fact]
    public async Task archiving_is_tenant_scoped_under_string_stream_identity()
    {
        await theFixture.ConfigureAsync(_stringIdentityConfiguration);
        await theFixture.CleanEventDataAsync();

        const string shared = "consignment/shared";

        await using (var a = await openForTenantAsync(TenantA))
        {
            EventsFor(a).StartStream<ComplianceConsignment>(shared, new ConsignmentBooked("Boston"),
                new ConsignmentScanned("Depot"));
            await SaveChangesAsync(a);
        }

        await using (var b = await openForTenantAsync(TenantB))
        {
            EventsFor(b).StartStream<ComplianceConsignment>(shared, new ConsignmentBooked("Lisbon"));
            await SaveChangesAsync(b);
        }

        await using (var a = await openForTenantAsync(TenantA))
        {
            EventsFor(a).ArchiveStream(shared);
            await SaveChangesAsync(a);
        }

        await using (var a = await openForTenantAsync(TenantA))
        {
            var mine = await EventsFor(a).FetchStreamStateAsync(shared, Cancellation);
            mine.ShouldNotBeNull();
            mine.IsArchived.ShouldBeTrue();
        }

        await using var b2 = await openForTenantAsync(TenantB);

        var theirs = await EventsFor(b2).FetchStreamStateAsync(shared, Cancellation);
        theirs.ShouldNotBeNull();
        theirs.IsArchived.ShouldBeFalse();

        var events = await EventsFor(b2).FetchStreamAsync(shared, token: Cancellation);
        events.ShouldHaveSingleItem().Data.ShouldBeOfType<ConsignmentBooked>().Destination.ShouldBe("Lisbon");
    }

    /// <summary>
    /// With the default tenant refused, every read route is still reachable — through a tenant scope
    /// — and the tenant-less routes fail the documented way (marten#5513 / jasperfx#885).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one fact in the library that asserts
    /// <see cref="ComplianceExceptionKind.DefaultTenantUsageDisabled" />, which had been declared and
    /// never used. Declaring an exception category nothing asserts is how a store ends up throwing
    /// something else, or nothing.
    /// </para>
    /// <para>
    /// <b>The reachability half is what matters, not the refusal.</b> A store that refuses the default
    /// tenant loudly and correctly is still broken if a surface has no tenant-scoped opener at all —
    /// that is jasperfx#885 exactly, where <see cref="IReadOnlyEventStore" /> was unreachable on a
    /// tenanted store because the only thing that produced one took no tenant, and the refusal
    /// happened at open time before any scope could be applied. So the fact walks the routes: a
    /// tenant-scoped session, the tenant-scoped reader, the tenant filter on
    /// <see cref="EventQuery" />, and <c>QueryStreamStates(tenantId)</c>. Each must answer; the
    /// refusal is asserted once, at the end, so that a store failing it has already proved the
    /// surface works.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task with_the_default_tenant_disabled_every_read_route_is_reachable_through_a_tenant_scope()
    {
        Assert.SkipUnless(theFixture.SupportsDisablingDefaultTenant,
            "This event store cannot be built with the default tenant refused.");

        await theFixture.ConfigureAsync(_defaultTenantDisabledConfiguration);
        await theFixture.CleanEventDataAsync();

        var shared = Guid.NewGuid();

        await appendAsync(TenantA, shared, new ConsignmentBooked("Boston"), new ConsignmentScanned("Depot"));
        await appendAsync(TenantB, shared, new ConsignmentBooked("Lisbon"));

        // 1. A tenant-scoped session.
        await using (var a = await openForTenantAsync(TenantA))
        {
            var events = await EventsFor(a).FetchStreamAsync(shared, token: Cancellation);
            events.Count.ShouldBe(2);
        }

        // 2. The tenant-scoped read-only tier, and its tenant-less members within that scope.
        var forA = theFixture.EventStore.OpenReadOnlyEventStore(TenantA);

        var state = await forA.FetchStreamStateAsync(shared, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(2);

        // 3. The tenant filter on the cross-stream query, from within that same scope.
        var queried = await forA.QueryEventsAsync(
            new EventQuery { TenantId = TenantA, PageSize = 1000 }, Cancellation);
        queried.TotalCount.ShouldBe(2);

        // 4. The stream-state queryable.
        var streams = await Documents.DocumentQueryableExtensions.ToListAsync(
            forA.QueryStreamStates(TenantA), Cancellation);
        streams.ShouldHaveSingleItem().Id.ShouldBe(shared);

        // Only now the refusal, so a store that fails this has already shown the surface is reachable.
        await ShouldFailWithAsync(ComplianceExceptionKind.DefaultTenantUsageDisabled, async () =>
        {
            await using var session = OpenSession();
            EventsFor(session).StartStream<ComplianceConsignment>(Guid.NewGuid(),
                new ConsignmentBooked("Nowhere"));
            await SaveChangesAsync(session);
        });
    }

    /// <summary>
    /// A message a projection publishes from <c>RaiseSideEffects</c> carries the tenant of the events
    /// that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ProjectionSideEffectCompliance</c> publishes only under the default tenant, so the tenant
    /// argument on <c>IMessageSink.PublishAsync&lt;T&gt;(T, string)</c> was never asserted — and the
    /// shared <see cref="RecordingMessageOutbox" /> received it and threw it away, which is a fair
    /// picture of how easy it is to lose.
    /// </para>
    /// <para>
    /// Losing it is not a cosmetic defect. A published side effect is the one thing a projection emits
    /// that leaves the store's transactional boundary, so the tenant on it is what decides which
    /// database the downstream handler writes to. A message published under the wrong tenant — or
    /// under the default one, which is the likely failure — is delivered, handled and persisted
    /// somewhere else entirely, and nothing inside the event store ever reports a problem.
    /// </para>
    /// <para>
    /// Both directions again, and the second one is what makes it an isolation fact rather than a
    /// metadata fact: two tenants each mann a watchtower, and each tenant's message must carry its own
    /// id. A store that stamped every message with one hard-coded tenant passes a single-tenant
    /// assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task projection_side_effect_messages_carry_the_event_tenant()
    {
        SkipUnlessDaemonIsSupported();

        Assert.SkipUnless(theFixture.SupportsMessageOutbox,
            "This event store has no message outbox for projection side effects.");

        await theFixture.ConfigureAsync(_outboxConfiguration);
        await theFixture.CleanEventDataAsync();

        _outbox.Reset();

        var shared = Guid.NewGuid();

        await using (var a = await openForTenantAsync(TenantA))
        {
            EventsFor(a).StartStream<ComplianceWatchtower>(shared, new WatchtowerManned("Amon Din"));
            await SaveChangesAsync(a);
        }

        await using (var b = await openForTenantAsync(TenantB))
        {
            EventsFor(b).StartStream<ComplianceWatchtower>(shared, new WatchtowerManned("Eilenach"));
            await SaveChangesAsync(b);
        }

        await StartDaemonAsync();

        // Deliberately not WaitForNonStaleProjectionDataAsync, for the reason
        // ProjectionSideEffectCompliance gives: the same projection RAISES an event, which takes a
        // sequence of its own, so the shard goes stale again the instant it writes one -- and
        // non-stale is therefore true in the window after the original events were folded and before
        // the side effects exist. That window is exactly where this fact would read.
        await waitForStreamVersionAsync(TenantA, shared, 2);
        await waitForStreamVersionAsync(TenantB, shared, 2);

        var published = _outbox.PublishedWithTenant
            .Where(x => x.Message is WatchtowerReported)
            .ToArray();

        // Nonzero first, for the reason every fact touching this outbox states: a store that dropped
        // the side effects entirely satisfies every claim below vacuously.
        published.Length.ShouldBe(2);

        published.Single(x => ((WatchtowerReported)x.Message).Name == "Amon Din").TenantId.ShouldBe(TenantA);
        published.Single(x => ((WatchtowerReported)x.Message).Name == "Eilenach").TenantId.ShouldBe(TenantB);
    }

    /// <summary>
    /// An async rebuild of a conjoined single stream projection produces one document per tenant,
    /// across hundreds of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The daemon statement of the same identity rule, and it needs its own fact because a rebuild is
    /// not the append path replayed. A rebuild tears the read side down and repopulates it from the
    /// events, at scale and out of the transaction that wrote them, so the tenant has to be
    /// reconstructed from each event rather than inherited from a session — and the teardown is its
    /// own hazard: a wipe scoped to the projection but not to the tenant deletes every tenant's rows
    /// on the first shard to run.
    /// </para>
    /// <para>
    /// <b>One stream id shared by all <see cref="RebuildTenantCount" /> tenants</b>, which is what
    /// makes the count an assertion rather than a smoke test. A correct store ends with that many
    /// documents carrying one id; a store that keys the read side on the stream id alone ends with
    /// exactly one, whatever the tenant count. And the number is large deliberately — two tenants fit
    /// inside a single daemon batch, where a grouping bug has nowhere to show itself.
    /// </para>
    /// <para>
    /// Every tenant is read back rather than a sample. A store that loses <em>some</em> tenants to a
    /// batch boundary is the interesting failure, and sampling three of two hundred is how it gets
    /// missed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task async_rebuild_across_many_tenants_produces_one_document_per_tenant_stream()
    {
        SkipUnlessDaemonIsSupported();

        var shared = Guid.NewGuid();
        var tenants = Enumerable.Range(0, RebuildTenantCount).Select(i => $"tenant{i:D4}").ToArray();

        foreach (var tenant in tenants)
        {
            await appendAsync(tenant, shared, new ConsignmentBooked(tenant), new ConsignmentScanned("Depot"));
        }

        var daemon = await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await daemon.RebuildProjectionAsync<ComplianceConsignmentLedger>(Cancellation);

        foreach (var tenant in tenants)
        {
            await using var session = await openForTenantAsync(tenant);

            var ledger = await LoadDocumentAsync<ComplianceConsignmentLedger>(session, shared);

            ledger.ShouldNotBeNull($"Tenant {tenant} has no rebuilt snapshot for the shared stream id.");
            ledger.Destination.ShouldBe(tenant);
            ledger.ScanCount.ShouldBe(1);
        }
    }
}
