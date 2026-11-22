using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Mapped natural key aggregates

/// <summary>
/// An async view of an order stream, reachable by the order's natural keys without owning a lookup of
/// its own: the order number resolves through <see cref="ComplianceNaturalKeyOrder"/>'s lookup and the
/// invoice code through <see cref="ComplianceNaturalKeyInvoice"/>'s, both maintained inline.
/// </summary>
/// <remarks>
/// Deliberately has <em>only</em> mapped keys. A type that owns a key gets an assembly-level
/// <see cref="NaturalKeyAggregateAttribute"/> and may be auto-registered by any store compiled with this
/// package (see <see cref="NaturalKeyCompliance{TFixture,TOperations,TQuerySession}"/>); a mapped-only
/// type gets none, so it is registered by this suite's configuration alone.
/// </remarks>
public partial class ComplianceMappedOrderSummary
{
    public Guid Id { get; set; }

    [MappedNaturalKey<ComplianceNaturalKeyOrder>]
    public ComplianceOrderNumber Number { get; set; }

    [MappedNaturalKey<ComplianceNaturalKeyInvoice>]
    public string InvoiceCode { get; set; } = string.Empty;

    public int Items { get; set; }
    public decimal Total { get; set; }

    public static ComplianceMappedOrderSummary Create(NaturalKeyOrderPlaced e) => new() { Number = e.Number };

    public void Apply(NaturalKeyOrderItemAdded e)
    {
        Items++;
        Total += e.Price;
    }

    public void Apply(NaturalKeyOrderRenumbered e) => Number = e.Number;

    public void Apply(NaturalKeyInvoiceRaised e) => InvoiceCode = e.Code;
}

#endregion

/// <summary>
/// Mapped natural keys: an aggregate fetched by a natural key whose lookup another projection owns,
/// which is how an <i>async</i> aggregate becomes reachable by natural key. The owner maintains the
/// lookup inline, so it is current the moment the events commit, while the async aggregate itself is
/// eventually consistent.
/// </summary>
/// <remarks>
/// <para>
/// Every fact registers <see cref="ComplianceMappedOrderSummary"/> <see cref="SnapshotLifecycle.Async"/>
/// against inline owners. The async registration is the point: the summary owns no lookup of its own,
/// so the only way these fetches can resolve is through the owners' lookups, and the summary's stored
/// snapshot lags its stream until a daemon catches up.
/// </para>
/// <para>
/// Most facts run without a daemon. The shared contract of <c>FetchLatest</c> and
/// <c>FetchForWriting</c> on an async aggregate is the aggregate's current state, whether or not a
/// daemon has caught up, so the store has to resolve the stream through the owner's lookup and then
/// aggregate it. <see cref="a_mapped_key_resolves_the_stored_snapshot_after_a_rebuild"/> covers the
/// stored-snapshot path.
/// </para>
/// <para>
/// Gated on both <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsNaturalKeys"/>
/// and <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsMappedNaturalKeys"/>.
/// It is a separate suite from <see cref="NaturalKeyCompliance{TFixture,TOperations,TQuerySession}"/> so
/// that a store enrolls when it implements mapped keys, without failing the owned-key facts it
/// already passes.
/// </para>
/// </remarks>
public abstract class MappedNaturalKeyCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static void addEventTypes(ComplianceStoreConfig config)
    {
        config.AddEventType<NaturalKeyOrderPlaced>();
        config.AddEventType<NaturalKeyOrderItemAdded>();
        config.AddEventType<NaturalKeyOrderRenumbered>();
        config.AddEventType<NaturalKeyOrderCompleted>();
        config.AddEventType<NaturalKeyInvoiceRaised>();
    }

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_mapped_natural_key";
        addEventTypes(config);
        config.RegisterValueType<ComplianceOrderNumber>();
        config.Snapshot<ComplianceNaturalKeyOrder>(SnapshotLifecycle.Inline);
        config.Snapshot<ComplianceNaturalKeyInvoice>(SnapshotLifecycle.Inline);
        config.Snapshot<ComplianceMappedOrderSummary>(SnapshotLifecycle.Async);
    };

    private static readonly Action<ComplianceStoreConfig> _tenantedConfiguration = config =>
    {
        config.SchemaName = "compliance_mapped_natural_key_tenanted";
        config.ConjoinedEventTenancy = true;
        addEventTypes(config);
        config.RegisterValueType<ComplianceOrderNumber>();
        config.Snapshot<ComplianceNaturalKeyOrder>(SnapshotLifecycle.Inline);
        config.Snapshot<ComplianceNaturalKeyInvoice>(SnapshotLifecycle.Inline);
        config.Snapshot<ComplianceMappedOrderSummary>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private bool isSupported => theFixture.SupportsNaturalKeys && theFixture.SupportsMappedNaturalKeys;

    private void SkipUnlessMappedNaturalKeysAreSupported()
    {
        Assert.SkipUnless(isSupported,
            "This event store does not resolve mapped natural keys through their source projection");
    }

    /// <summary>
    /// Skips configuration while the gate is closed, for the same reason as
    /// <see cref="NaturalKeyCompliance{TFixture,TOperations,TQuerySession}"/>: a store without the
    /// feature may fail while building a store that declares it.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync();

        if (!isSupported) return;

        await theFixture.ConfigureAsync(Configuration);
        await theFixture.CleanEventDataAsync();
    }

    private async Task<Guid> anOrderAsync(TOperations session, ComplianceOrderNumber number, params object[] more)
    {
        var streamId = Guid.NewGuid();

        EventsFor(session).StartStream<ComplianceNaturalKeyOrder>(streamId,
            new object[] { new NaturalKeyOrderPlaced(number, "Alice") }.Concat(more).ToArray());
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<Guid> anOrderAsync(ComplianceOrderNumber number, params object[] more)
    {
        await using var session = OpenSession();
        return await anOrderAsync(session, number, more);
    }

    private static ComplianceOrderNumber aNumber(string value) => new($"{value}-{Guid.NewGuid():N}");

    [Fact]
    public async Task fetch_latest_async_aggregate_by_mapped_key()
    {
        SkipUnlessMappedNaturalKeysAreSupported();

        var number = aNumber("ORD");
        var streamId = await anOrderAsync(number,
            new NaturalKeyOrderItemAdded("Widget", 9.99m), new NaturalKeyOrderItemAdded("Gadget", 5.01m));

        await using var session = OpenSession();
        var summary = await EventsFor(session)
            .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(number, Cancellation);

        summary.ShouldNotBeNull();
        summary.Id.ShouldBe(streamId);
        summary.Number.ShouldBe(number);
        summary.Items.ShouldBe(2);
        summary.Total.ShouldBe(15.00m);
    }

    [Fact]
    public async Task fetch_latest_returns_null_for_a_nonexistent_mapped_key()
    {
        SkipUnlessMappedNaturalKeysAreSupported();

        await using var session = OpenSession();
        var summary = await EventsFor(session)
            .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(aNumber("ORD-NOPE"), Cancellation);

        summary.ShouldBeNull();
    }

    [Fact]
    public async Task fetch_for_writing_async_aggregate_by_mapped_key_and_append()
    {
        SkipUnlessMappedNaturalKeysAreSupported();

        var number = aNumber("ORD");
        var streamId = await anOrderAsync(number, new NaturalKeyOrderItemAdded("Widget", 10.00m));

        await using (var session = OpenSession())
        {
            var stream = await EventsFor(session)
                .FetchForWriting<ComplianceMappedOrderSummary, ComplianceOrderNumber>(number, Cancellation);

            stream.Id.ShouldBe(streamId);
            stream.Aggregate.ShouldNotBeNull();
            stream.Aggregate.Total.ShouldBe(10.00m);

            stream.AppendOne(new NaturalKeyOrderItemAdded("Gadget", 2.50m));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var summary = await EventsFor(query)
            .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(number, Cancellation);

        summary.ShouldNotBeNull();
        summary.Items.ShouldBe(2);
        summary.Total.ShouldBe(12.50m);
    }

    /// <summary>
    /// The owner retires a renamed key from its lookup, and a mapped key resolves through that same
    /// lookup, so it follows the rename with no bookkeeping of its own.
    /// </summary>
    [Fact]
    public async Task a_mapped_key_follows_a_rename_by_the_owner()
    {
        SkipUnlessMappedNaturalKeysAreSupported();

        var original = aNumber("ORD-OLD");
        var renumbered = aNumber("ORD-NEW");
        var streamId = await anOrderAsync(original);

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new NaturalKeyOrderRenumbered(renumbered));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();

        var summary = await EventsFor(query)
            .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(renumbered, Cancellation);
        summary.ShouldNotBeNull();
        summary.Id.ShouldBe(streamId);
        summary.Number.ShouldBe(renumbered);

        (await EventsFor(query)
                .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(original, Cancellation))
            .ShouldBeNull();
    }

    /// <summary>
    /// The lookup is chosen per key type: the order number through the order's lookup and the
    /// invoice code through the invoice's, on the same stream and the same aggregate.
    /// </summary>
    [Fact]
    public async Task each_mapped_key_resolves_through_its_own_source()
    {
        SkipUnlessMappedNaturalKeysAreSupported();

        var number = aNumber("ORD");
        var invoiceCode = $"INV-{Guid.NewGuid():N}";
        var streamId = await anOrderAsync(number,
            new NaturalKeyInvoiceRaised(invoiceCode, 42.00m), new NaturalKeyOrderItemAdded("Widget", 42.00m));

        await using var session = OpenSession();

        var byNumber = await EventsFor(session)
            .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(number, Cancellation);
        byNumber.ShouldNotBeNull();
        byNumber.Id.ShouldBe(streamId);

        var byInvoice = await EventsFor(session)
            .FetchLatest<ComplianceMappedOrderSummary, string>(invoiceCode, Cancellation);
        byInvoice.ShouldNotBeNull();
        byInvoice.Id.ShouldBe(streamId);
        byInvoice.InvoiceCode.ShouldBe(invoiceCode);
        byInvoice.Total.ShouldBe(42.00m);
    }

    /// <summary>
    /// The stored-snapshot path: once the daemon has written the async snapshot, a mapped key still
    /// resolves to it, and the snapshot reflects every event.
    /// </summary>
    [Fact]
    public async Task a_mapped_key_resolves_the_stored_snapshot_after_a_rebuild()
    {
        SkipUnlessMappedNaturalKeysAreSupported();

        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        var number = aNumber("ORD-REBUILD");
        var streamId = await anOrderAsync(number, new NaturalKeyOrderItemAdded("Widget", 7.00m));

        var daemon = await StartDaemonAsync();
        await daemon.RebuildProjectionAsync<ComplianceMappedOrderSummary>(Cancellation);

        await using var query = OpenSession();
        var summary = await EventsFor(query)
            .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(number, Cancellation);

        summary.ShouldNotBeNull();
        summary.Id.ShouldBe(streamId);
        summary.Total.ShouldBe(7.00m);
    }

    [Fact]
    public async Task a_mapped_key_is_isolated_by_tenant()
    {
        SkipUnlessMappedNaturalKeysAreSupported();

        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store cannot slice one database by tenant");

        await theFixture.ConfigureAsync(_tenantedConfiguration);
        await theFixture.CleanEventDataAsync();

        var shared = aNumber("ORD-SHARED");

        Guid acme;
        await using (var session = await openForTenantAsync("acme"))
        {
            acme = await anOrderAsync(session, shared, new NaturalKeyOrderItemAdded("Anvil", 1.00m));
        }

        Guid globex;
        await using (var session = await openForTenantAsync("globex"))
        {
            globex = await anOrderAsync(session, shared, new NaturalKeyOrderItemAdded("Hammock", 2.00m));
        }

        await using (var session = await openForTenantAsync("acme"))
        {
            var summary = await EventsFor(session)
                .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(shared, Cancellation);
            summary.ShouldNotBeNull();
            summary.Id.ShouldBe(acme);
            summary.Total.ShouldBe(1.00m);
        }

        await using var other = await openForTenantAsync("globex");
        var theirs = await EventsFor(other)
            .FetchLatest<ComplianceMappedOrderSummary, ComplianceOrderNumber>(shared, Cancellation);
        theirs.ShouldNotBeNull();
        theirs.Id.ShouldBe(globex);
        theirs.Total.ShouldBe(2.00m);
    }

    private async Task<TOperations> openForTenantAsync(string tenantId)
    {
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;
        var databases = await theFixture.EventStore.AllDatabases();
        return store.OpenSession(databases.First(), tenantId);
    }
}
