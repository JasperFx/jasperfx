using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Descriptors;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Multi-database explorer events

public record ManifestFiled(string Port);

public record ManifestAmended(string Note);

#endregion

/// <summary>
/// The shared machinery for the two multi-database explorer arms (jasperfx#810) — appending into a
/// named tenant's database, and reading back through each of the three scopings the explorer offers.
/// </summary>
/// <remarks>
/// <para>
/// The explorer reads were scoped by <em>tenant</em> only, which leaves two gaps on a store with more
/// than one database. The database-scoped overloads shipped in #817; what was still missing was any
/// arm that runs against a store where there <em>is</em> more than one database, so nothing held
/// either gap to a definition.
/// </para>
/// <para>
/// Split into two concrete suites rather than one, because the two gaps live on independent axes and
/// a single configuration cannot express both. Database-per-tenant has no co-located tenants, so it
/// cannot see a dropped <c>tenant_id</c> predicate; sharded tenancy has co-located tenants, so it
/// cannot isolate "the wrong database answered" from "the right database answered about the wrong
/// tenant".
/// </para>
/// </remarks>
public abstract class MultiDatabaseExplorerComplianceBase<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    /// <summary>
    /// Deliberately larger than anything a fact appends, so nothing here can pass or fail because of
    /// where a page boundary fell.
    /// </summary>
    protected const int Plenty = 100;

    protected void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsExplorerSurface,
            "This event store does not implement the event store explorer surface.");
        Assert.SkipUnless(theFixture.SupportsMultipleDatabases,
            "This event store fixture cannot build a store backed by more than one database.");
    }

    protected IEventStore<TOperations, TQuerySession> TheStore
        => (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;

    protected ValueTask<IEventDatabase> DatabaseForAsync(string tenantId)
        => theFixture.DatabaseForTenantAsync(tenantId);

    /// <summary>
    /// Append a stream under one tenant, into that tenant's own database.
    /// </summary>
    protected async Task AppendAsync(string tenantId, Guid streamId, params object[] events)
    {
        var database = await DatabaseForAsync(tenantId);

        await using var session = TheStore.OpenSession(database, tenantId);
        EventsFor(session).StartStream(streamId, events);
        await SaveChangesAsync(session);
    }

    protected async Task<IReadOnlyList<string>> StreamIdsAsync(
        Func<Task<IReadOnlyList<StreamSummary>>> read)
        => (await read()).Select(x => x.StreamId).ToList();

    protected async Task<IReadOnlyList<long>> StreamVersionsAsync(
        Func<IAsyncEnumerable<EventRecord>> read)
    {
        var versions = new List<long>();
        await foreach (var e in read())
        {
            versions.Add(e.StreamVersion);
        }

        return versions;
    }

    /// <summary>
    /// The store reports the databases the configuration asked for, and says so through
    /// <see cref="IEventStore.DatabaseCardinality" />.
    /// </summary>
    /// <remarks>
    /// A precondition rather than a finding, and it is here so that the isolation facts below cannot
    /// pass <em>vacuously</em>. A fixture that declared
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsMultipleDatabases" />
    /// and then built one database satisfies every "and not the other one's data" assertion by having
    /// no other one — which is precisely the shape of store these arms exist to stop being tested
    /// against.
    /// </remarks>
    [Fact]
    public async Task the_store_is_genuinely_backed_by_more_than_one_database()
    {
        SkipUnlessSupported();

        var databases = await EventStore.AllDatabases();

        databases.Count.ShouldBeGreaterThanOrEqualTo(2,
            "The fixture declared SupportsMultipleDatabases but AllDatabases() reports fewer than two, so every isolation fact in this suite would pass vacuously.");

        EventStore.DatabaseCardinality.ShouldNotBe(DatabaseCardinality.Single,
            "A store backed by several databases reporting DatabaseCardinality.Single sends every database-scoped explorer read down the single-database shortcut.");
    }
}

/// <summary>
/// <b>Gap 1.</b> The explorer reads against a database-per-tenant store: each tenant has a database
/// of its own, and a store-global read has to say so rather than answering from one of them
/// (jasperfx#810).
/// </summary>
/// <remarks>
/// <para>
/// <c>GetRecentStreamsAsync(count, tenantId: null, ct)</c> means "store-global" in the contract. On a
/// store whose cardinality is not <see cref="DatabaseCardinality.Single" />, Marten's implementation
/// opens <c>openExplorerSession()</c> — one session, one database — and the result looks exactly like
/// a complete answer. CritterWatch#1231 is that in production: a console over <b>512 shard databases
/// and 2,173 tenants</b> lists recent streams from one database when no tenant is selected, with
/// nothing in the result saying which.
/// </para>
/// <para>
/// The suite accepts <em>either</em> remedy, because both are defensible and the contract says so:
/// fan out across <see cref="IEventStore.AllDatabases" /> and merge, or throw pointing at the
/// database-scoped overload. What it refuses is the third answer — one database's worth of rows,
/// returned as though it were everything.
/// </para>
/// </remarks>
public abstract class DatabasePerTenantExplorerCompliance<TFixture, TOperations, TQuerySession>
    : MultiDatabaseExplorerComplianceBase<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_db_per_tenant";

        config.AddEventType<ManifestFiled>();
        config.AddEventType<ManifestAmended>();

        // Database per tenant: distinct names, and deliberately NOT conjoined -- there is no
        // tenant_id column here, which is what makes this arm blind to gap 2 and why the sharded arm
        // exists separately.
        config.AssignTenantToDatabase(TenantA, "compliance_tenant_a");
        config.AssignTenantToDatabase(TenantB, "compliance_tenant_b");
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// A database-scoped listing answers about that database and no other.
    /// </summary>
    /// <remarks>
    /// Asserted in both directions. A read that returned everything would satisfy the "contains its
    /// own" half on both databases and be exactly as wrong as one that returned nothing.
    /// </remarks>
    [Fact]
    public async Task a_database_scoped_listing_answers_only_that_database()
    {
        SkipUnlessSupported();

        var inA = Guid.NewGuid();
        var inB = Guid.NewGuid();

        await AppendAsync(TenantA, inA, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, inB, new ManifestFiled("Singapore"));

        var databaseA = await DatabaseForAsync(TenantA);
        var databaseB = await DatabaseForAsync(TenantB);

        var fromA = await StreamIdsAsync(() =>
            EventStore.GetRecentStreamsAsync(databaseA, Plenty, null, Cancellation));

        fromA.ShouldContain(inA.ToString());
        fromA.ShouldNotContain(inB.ToString());

        var fromB = await StreamIdsAsync(() =>
            EventStore.GetRecentStreamsAsync(databaseB, Plenty, null, Cancellation));

        fromB.ShouldContain(inB.ToString());
        fromB.ShouldNotContain(inA.ToString());
    }

    /// <summary>
    /// A database-scoped stream read does not see a same-identified stream in another database.
    /// </summary>
    /// <remarks>
    /// The same stream id in both databases, with different event counts so the two answers cannot be
    /// confused. On a database-per-tenant store a stream id is unique only within its database, and a
    /// read that resolved the wrong one returns a plausible, complete-looking, wrong stream.
    /// </remarks>
    [Fact]
    public async Task a_database_scoped_stream_read_does_not_see_another_databases_stream()
    {
        SkipUnlessSupported();

        var shared = Guid.NewGuid();

        await AppendAsync(TenantA, shared, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, shared, new ManifestFiled("Singapore"), new ManifestAmended("re-routed"));

        var databaseA = await DatabaseForAsync(TenantA);
        var databaseB = await DatabaseForAsync(TenantB);

        var fromA = await StreamVersionsAsync(() =>
            EventStore.ReadStreamAsync(databaseA, shared.ToString(), null, Cancellation));
        var fromB = await StreamVersionsAsync(() =>
            EventStore.ReadStreamAsync(databaseB, shared.ToString(), null, Cancellation));

        fromA.ShouldBe([1L]);
        fromB.ShouldBe([1L, 2L]);
    }

    /// <summary>
    /// A database-scoped metadata read reports that database's version of the stream.
    /// </summary>
    [Fact]
    public async Task a_database_scoped_metadata_read_answers_only_that_database()
    {
        SkipUnlessSupported();

        var shared = Guid.NewGuid();

        await AppendAsync(TenantA, shared, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, shared, new ManifestFiled("Singapore"), new ManifestAmended("re-routed"));

        var metadataA = await EventStore.GetStreamMetadataAsync(
            await DatabaseForAsync(TenantA), shared.ToString(), null, Cancellation);
        var metadataB = await EventStore.GetStreamMetadataAsync(
            await DatabaseForAsync(TenantB), shared.ToString(), null, Cancellation);

        metadataA.ShouldNotBeNull();
        metadataB.ShouldNotBeNull();

        metadataA.Version.ShouldBe(1);
        metadataB.Version.ShouldBe(2);
    }

    /// <summary>
    /// <b>The headline fact.</b> A store-global listing on a multi-database store is not a silent
    /// partial answer: it either covers every database or it refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both remedies are accepted deliberately — the choice between merging and refusing is a product
    /// decision, and pinning one of them here would be inventing a contract rather than closing a
    /// gap. What is pinned is that the third answer is not available.
    /// </para>
    /// <para>
    /// The assertion is written as "contains both or threw" rather than counting rows, so a store
    /// that fans out and applies its own per-database cap still passes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task a_store_global_listing_is_not_a_silent_partial_answer()
    {
        SkipUnlessSupported();

        var inA = Guid.NewGuid();
        var inB = Guid.NewGuid();

        await AppendAsync(TenantA, inA, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, inB, new ManifestFiled("Singapore"));

        IReadOnlyList<string> ids;
        try
        {
            ids = await StreamIdsAsync(() => EventStore.GetRecentStreamsAsync(Plenty, null, Cancellation));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            // The refusing remedy. A caller that gets this goes to the database-scoped overload,
            // which is the outcome the fan-out remedy reaches by a different route.
            return;
        }

        ids.ShouldContain(inA.ToString());
        ids.ShouldContain(inB.ToString(),
            "A store-global listing returned one database's streams and not the other's. On a store with 512 databases that result is indistinguishable from a complete answer (CritterWatch#1231): either fan out across AllDatabases(), or throw pointing at the database-scoped overload.");
    }

    /// <summary>
    /// The same rule for the stream read, where getting it wrong is sharper still: the same stream id
    /// exists in both databases, so the store-global read does not merely return <em>less</em> than
    /// everything, it returns one plausible answer out of two.
    /// </summary>
    [Fact]
    public async Task a_store_global_stream_read_is_not_a_silent_partial_answer()
    {
        SkipUnlessSupported();

        var shared = Guid.NewGuid();

        await AppendAsync(TenantA, shared, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, shared, new ManifestFiled("Singapore"), new ManifestAmended("re-routed"));

        IReadOnlyList<long> versions;
        try
        {
            versions = await StreamVersionsAsync(() =>
                EventStore.ReadStreamAsync(shared.ToString(), null, Cancellation));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return;
        }

        // Either everything (three events across the two databases) or a refusal. One database's
        // worth is the answer being ruled out.
        versions.Count.ShouldBe(3,
            $"A store-global stream read returned {versions.Count} events for a stream id that exists in two databases, so it answered from one of them and said nothing about the other.");
    }

    /// <summary>
    /// A tenant-scoped read routes to that tenant's database. The half that already worked, kept so a
    /// fix for the facts above cannot regress it.
    /// </summary>
    [Fact]
    public async Task a_tenant_scoped_listing_answers_from_that_tenants_database()
    {
        SkipUnlessSupported();

        var inA = Guid.NewGuid();
        var inB = Guid.NewGuid();

        await AppendAsync(TenantA, inA, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, inB, new ManifestFiled("Singapore"));

        var forA = await StreamIdsAsync(() =>
            EventStore.GetRecentStreamsAsync(Plenty, TenantA, Cancellation));

        forA.ShouldContain(inA.ToString());
        forA.ShouldNotContain(inB.ToString());
    }
}

/// <summary>
/// <b>Gap 2.</b> The explorer reads against a <em>sharded</em> store — a pool of databases with many
/// tenants co-located in each, conjoined. This is the arm that pins the <c>tenant_id</c> predicate
/// (jasperfx#810).
/// </summary>
/// <remarks>
/// <para>
/// Marten decides between the two tenancy models by cardinality:
/// </para>
/// <code>
/// var spansSeveralDatabases = Options.Tenancy.Cardinality != DatabaseCardinality.Single;
/// var scopeByColumn = tenantId != null &amp;&amp; !spansSeveralDatabases;
/// </code>
/// <para>
/// So a multi-database store opens the tenant's database and applies <b>no</b> <c>tenant_id</c>
/// filter, on the stated premise that every stream in that database already belongs to the tenant.
/// That holds for database-per-tenant. It does <em>not</em> hold for sharded tenancy, which by its own
/// summary distributes tenants across a pool of databases with conjoined tenancy — many tenants share
/// each database there. Cardinality and tenancy style are two axes, and this is the cell where they
/// disagree.
/// </para>
/// <para>
/// The issue filed this as a code-read rather than a reproduction. That is exactly what a compliance
/// arm is for: it settles the question in whichever direction it turns out, on every store at once,
/// instead of one maintainer's reading of one store's source.
/// </para>
/// <para>
/// <b>Three tenants, two databases.</b> Two co-located so a leak has somewhere to come from, and a
/// third alone in the second database so the store's cardinality is genuinely not
/// <see cref="DatabaseCardinality.Single" /> — which is the whole precondition, since it is the
/// cardinality test that turns the predicate off.
/// </para>
/// </remarks>
public abstract class ShardedTenancyExplorerCompliance<TFixture, TOperations, TQuerySession>
    : MultiDatabaseExplorerComplianceBase<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string TenantA = "acme";

    /// <summary>Co-located with <see cref="TenantA" /> — the tenant a leak leaks from.</summary>
    private const string TenantB = "globex";

    /// <summary>Alone in the second shard, so the store spans several databases.</summary>
    private const string TenantC = "initech";

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_sharded";
        config.ConjoinedEventTenancy = true;

        config.AddEventType<ManifestFiled>();
        config.AddEventType<ManifestAmended>();

        config.AssignTenantToDatabase(TenantA, "compliance_shard_one");
        config.AssignTenantToDatabase(TenantB, "compliance_shard_one");
        config.AssignTenantToDatabase(TenantC, "compliance_shard_two");
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// The positive control, and it has to come first: a database-scoped read with no tenant really
    /// does see every co-located tenant.
    /// </summary>
    /// <remarks>
    /// Without it, the isolation facts below are satisfied by a store that returns nothing at all —
    /// and "the read is broken" would be indistinguishable from "the read is correctly scoped". This
    /// fact is also the statement that database-global and tenant-global are different questions on a
    /// sharded store, which is the thing cardinality-based scoping collapses.
    /// </remarks>
    [Fact]
    public async Task a_database_scoped_listing_with_no_tenant_sees_every_co_located_tenant()
    {
        SkipUnlessSupported();

        var forA = Guid.NewGuid();
        var forB = Guid.NewGuid();

        await AppendAsync(TenantA, forA, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, forB, new ManifestFiled("Singapore"));

        var shard = await DatabaseForAsync(TenantA);

        var ids = await StreamIdsAsync(() =>
            EventStore.GetRecentStreamsAsync(shard, Plenty, null, Cancellation));

        ids.ShouldContain(forA.ToString());
        ids.ShouldContain(forB.ToString(),
            "Two tenants share this database, so a database-scoped read with no tenant must see both. A store answering only one of them is scoping by something it was not asked to scope by.");
    }

    /// <summary>
    /// <b>The fact the issue could not reproduce.</b> A tenant-scoped listing on a shared database
    /// does not return a co-located tenant's streams.
    /// </summary>
    [Fact]
    public async Task a_tenant_scoped_listing_does_not_leak_a_co_located_tenants_streams()
    {
        SkipUnlessSupported();

        var forA = Guid.NewGuid();
        var forB = Guid.NewGuid();

        await AppendAsync(TenantA, forA, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, forB, new ManifestFiled("Singapore"));

        var ids = await StreamIdsAsync(() =>
            EventStore.GetRecentStreamsAsync(Plenty, TenantA, Cancellation));

        ids.ShouldContain(forA.ToString());
        ids.ShouldNotContain(forB.ToString(),
            "A tenant-scoped listing returned a co-located tenant's stream. On a sharded store the tenant's database is not the tenant's data: the tenant_id predicate is needed as well as the database, and deciding from cardinality alone drops it.");
    }

    /// <summary>
    /// The sharpest form: the same stream id under two co-located tenants. A read that drops the
    /// predicate does not merely return extra rows — it returns both tenants' events interleaved into
    /// one version-ordered sequence, which reads as a single stream that has been tampered with.
    /// </summary>
    [Fact]
    public async Task a_tenant_scoped_stream_read_does_not_leak_a_co_located_tenants_events()
    {
        SkipUnlessSupported();

        var shared = Guid.NewGuid();

        await AppendAsync(TenantA, shared, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, shared, new ManifestFiled("Singapore"), new ManifestAmended("re-routed"));

        var versions = await StreamVersionsAsync(() =>
            EventStore.ReadStreamAsync(shared.ToString(), TenantA, Cancellation));

        versions.ShouldBe([1L],
            "A tenant-scoped stream read returned a co-located tenant's events. Under conjoined tenancy the identity of a stream is (tenant, id), not id alone.");
    }

    /// <summary>
    /// The metadata read is scoped the same way. Carried separately because it is a different query
    /// on every store, and jasperfx#810's audit list names it specifically — Marten's tenant-less path
    /// opens the default session.
    /// </summary>
    [Fact]
    public async Task tenant_scoped_stream_metadata_does_not_leak_a_co_located_tenants_stream()
    {
        SkipUnlessSupported();

        var shared = Guid.NewGuid();

        await AppendAsync(TenantA, shared, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, shared, new ManifestFiled("Singapore"), new ManifestAmended("re-routed"));

        var metadata = await EventStore.GetStreamMetadataAsync(shared.ToString(), TenantA, Cancellation);

        metadata.ShouldNotBeNull();
        metadata.Version.ShouldBe(1,
            "Tenant-scoped stream metadata reported a version that includes a co-located tenant's events.");
    }

    /// <summary>
    /// Naming the database as well as the tenant does not change the answer. Both dimensions apply;
    /// supplying one does not turn the other off.
    /// </summary>
    /// <remarks>
    /// This is the fact that says the two scopings compose rather than compete. A store that treats
    /// the database overload as "the scoping" and drops the tenant would pass every database-scoped
    /// fact in the sibling arm and fail here.
    /// </remarks>
    [Fact]
    public async Task a_database_and_tenant_scoped_read_applies_both()
    {
        SkipUnlessSupported();

        var forA = Guid.NewGuid();
        var forB = Guid.NewGuid();

        await AppendAsync(TenantA, forA, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantB, forB, new ManifestFiled("Singapore"));

        var shard = await DatabaseForAsync(TenantA);

        var ids = await StreamIdsAsync(() =>
            EventStore.GetRecentStreamsAsync(shard, Plenty, TenantA, Cancellation));

        ids.ShouldContain(forA.ToString());
        ids.ShouldNotContain(forB.ToString(),
            "Naming the database turned the tenant predicate off. The two scopings compose: the database says where to look, the tenant says which rows.");
    }

    /// <summary>
    /// A tenant in the other shard is not visible from this one, whichever way it is asked.
    /// </summary>
    /// <remarks>
    /// The cross-shard direction, which the co-located facts cannot cover: they would both pass on a
    /// store that filtered correctly by tenant and then queried every database in the pool.
    /// </remarks>
    [Fact]
    public async Task a_tenant_in_another_shard_is_not_visible_from_this_one()
    {
        SkipUnlessSupported();

        var forA = Guid.NewGuid();
        var forC = Guid.NewGuid();

        await AppendAsync(TenantA, forA, new ManifestFiled("Rotterdam"));
        await AppendAsync(TenantC, forC, new ManifestFiled("Valparaiso"));

        var shardOne = await DatabaseForAsync(TenantA);

        var ids = await StreamIdsAsync(() =>
            EventStore.GetRecentStreamsAsync(shardOne, Plenty, null, Cancellation));

        ids.ShouldContain(forA.ToString());
        ids.ShouldNotContain(forC.ToString());
    }
}
