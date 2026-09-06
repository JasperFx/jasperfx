using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Natural key aggregates and their events

/// <summary>
/// The business identifier a stream is reachable by, as a strong-typed wrapper.
/// </summary>
/// <remarks>
/// A <c>readonly record struct</c> over a single positional parameter, the same shape
/// <see cref="CompliancePaymentId"/> uses and for the same reason: it satisfies
/// <c>JasperFx.Core.Reflection.ValueTypeInfo.ForType</c> without taking a source-generator
/// dependency the library cannot require of a consumer. <c>NaturalKeyDefinition</c> unwraps it to
/// its <see cref="string"/> inner type, which is one of the three
/// <c>NaturalKeyDefinition.IsValid()</c> accepts.
/// </remarks>
public readonly record struct ComplianceOrderNumber(string Value);

/// <summary>
/// Carries the natural key as a property <em>of the natural key's own type</em>, which is the
/// second and most predictable of the extraction strategies
/// <c>JasperFxAggregationProjectionBase.buildExtractor</c> tries. Deliberately the only member of
/// that type on the event, so discovery has nothing to disambiguate.
/// </summary>
public record NaturalKeyOrderPlaced(ComplianceOrderNumber Number, string Customer);

public record NaturalKeyOrderItemAdded(string Item, decimal Price);

/// <summary>
/// Renumbering an order — the event that makes the natural key <em>mutable</em>, which is the
/// property that separates a natural key from a stream id.
/// </summary>
public record NaturalKeyOrderRenumbered(ComplianceOrderNumber Number);

public record NaturalKeyOrderCompleted;

public record NaturalKeyInvoiceRaised(string Code, decimal Amount);

/// <summary>
/// Guid-identity aggregate reachable by a wrapped natural key.
/// </summary>
public partial class ComplianceNaturalKeyOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public ComplianceOrderNumber Number { get; set; }

    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public bool Complete { get; set; }

    [NaturalKeySource]
    public static ComplianceNaturalKeyOrder Create(IEvent<NaturalKeyOrderPlaced> e) =>
        new() { Id = e.StreamId, Number = e.Data.Number, Customer = e.Data.Customer };

    public void Apply(NaturalKeyOrderItemAdded e) => Total += e.Price;

    [NaturalKeySource]
    public void Apply(NaturalKeyOrderRenumbered e) => Number = e.Number;

    public void Apply(NaturalKeyOrderCompleted _) => Complete = true;
}

/// <summary>
/// The same aggregate keyed by a <em>string</em> stream identity. A separate type because the
/// aggregate's <c>Id</c> has to agree with the store's stream identity, and that is a store-level
/// setting — the two halves cannot share a store, so they cannot share an aggregate either.
/// </summary>
public partial class ComplianceNaturalKeyOrderByKey
{
    public string Id { get; set; } = string.Empty;

    [NaturalKey]
    public ComplianceOrderNumber Number { get; set; }

    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }

    [NaturalKeySource]
    public static ComplianceNaturalKeyOrderByKey Create(IEvent<NaturalKeyOrderPlaced> e) =>
        new() { Id = e.StreamKey!, Number = e.Data.Number, Customer = e.Data.Customer };

    public void Apply(NaturalKeyOrderItemAdded e) => Total += e.Price;

    [NaturalKeySource]
    public void Apply(NaturalKeyOrderRenumbered e) => Number = e.Number;
}

/// <summary>
/// A natural key that is a bare <see cref="string"/> rather than a wrapper.
/// </summary>
/// <remarks>
/// Worth its own aggregate because <c>NaturalKeyDefinition</c> takes a visibly different path for
/// it — <c>IsPrimitiveKeyType</c> short-circuits before <c>ValueTypeInfo</c> is ever consulted, so
/// <c>Unwrap</c> becomes the identity function. A store that only ever exercised wrapped keys could
/// have an unwrap step that silently fails on the primitive.
/// </remarks>
public partial class ComplianceNaturalKeyInvoice
{
    public Guid Id { get; set; }

    [NaturalKey]
    public string Code { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    [NaturalKeySource]
    public static ComplianceNaturalKeyInvoice Create(IEvent<NaturalKeyInvoiceRaised> e) =>
        new() { Id = e.StreamId, Code = e.Data.Code, Amount = e.Data.Amount };
}

#endregion

/// <summary>
/// Natural keys — reaching an event stream by the business identifier its aggregate carries, rather
/// than by the surrogate stream id.
/// </summary>
/// <remarks>
/// <para>
/// jasperfx#764, and the largest un-lifted event-side cluster: Marten, Polecat and Fisher each carry
/// a natural key test file plus a run of regression tests around it, and nine fact names are
/// verbatim identical between Marten's <c>fetching_by_natural_key</c> and Polecat's
/// <c>natural_key_tests</c>.
/// </para>
/// <para>
/// <b>This suite adds no registrar seam</b>, which is worth stating because the natural key cluster
/// looks like it should need one. It does not: the <c>[NaturalKey]</c> / <c>[NaturalKeySource]</c>
/// attributes and <c>NaturalKeyDefinition</c> are already in <c>JasperFx.Events.Aggregation</c>,
/// discovery already runs inside the shared <c>JasperFxAggregationProjectionBase</c> that all three
/// products' aggregation projections derive from, and the fetch triple
/// (<c>FetchForWriting&lt;T,TId&gt;</c>, <c>FetchForExclusiveWriting&lt;T,TId&gt;</c>,
/// <c>FetchLatest&lt;T,TId&gt;</c>) is already on <see cref="IEventStoreOperations"/>. Registration
/// is the existing <see cref="ComplianceStoreConfig.Snapshot{TDoc}"/> — which is what every product
/// spells, whether the user writes <c>Snapshot&lt;T&gt;(Inline)</c> (Marten) or
/// <c>Add&lt;SingleStreamProjection&lt;T,TId&gt;&gt;(Inline)</c> (Polecat) — plus
/// <see cref="ComplianceStoreConfig.RegisterValueType{TValue}"/> for the wrapper. The attributes
/// land on the aggregate in the consumer's own compilation, exactly like <c>[BoundaryAggregate]</c>
/// in the DCB suite. All that varies between stores is the storage half, and that is behavior, so a
/// single <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsNaturalKeys"/>
/// gate carries it.
/// </para>
/// <para>
/// <b>Consumers should expect the aggregates above to be discovered eagerly.</b> The source
/// generator emits an assembly-level <see cref="NaturalKeyAggregateAttribute"/> for every type with
/// a <c>[NaturalKey]</c> property so a store can auto-register the snapshot and its lookup
/// infrastructure at startup. Unlike the DCB suite's <c>CourseLoad</c> — discovered lazily on first
/// use, and therefore free to sit unregistered — these three types may be registered by the store
/// merely by being compiled into the consumer, and
/// <see cref="ComplianceNaturalKeyOrderByKey"/> in particular is a string-identity aggregate that
/// would be auto-registered into Guid-identity stores. A consumer taking this package should watch
/// for that at store construction rather than assume the gate defends it.
/// </para>
/// <para>
/// <b>What was deliberately left behind.</b> The cluster contains more genuine cross-store
/// disagreement than the matching fact names suggest, and the divergences are load-bearing rather
/// than incidental:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>The miss on <c>FetchForWriting</c>.</b> Marten returns a handle with a null aggregate at
/// version 0 (<c>fetch_for_writing_new_stream_by_natural_key_returns_null_aggregate</c>); Polecat
/// throws <see cref="InvalidOperationException"/>; Fisher throws its own
/// <c>UnknownNaturalKeyException</c>. Three products, two irreconcilable contracts, so the fact is
/// excluded. Only the <c>FetchLatest</c> miss is agreed — Marten and Polecat both assert null —
/// and that is what every "does not resolve" assertion below is written against.
/// </description></item>
/// <item><description>
/// <b>Transactional rollback of the key row</b> (Fisher). The property is real and shared, but
/// provoking it needs a poisoned unit of work — Fisher's test queues raw SQL that fails the batch —
/// and no shared surface can fail a transaction on demand.
/// </description></item>
/// <item><description>
/// <b>Storage layout</b>: Marten's <c>Bug_4199_natural_key_table_not_found</c> and
/// <c>Bug_5044_natural_key_foreign_key_guard</c>, its <c>archived_partitioning_natural_key_tests</c>,
/// and Fisher's lookup-table column assertion are all DDL and partitioning, which the library keeps
/// permanently out of scope.
/// </description></item>
/// <item><description>
/// <b>The "live lifecycle" pair.</b> <c>live_fetch_for_writing_by_natural_key</c> and
/// <c>live_fetch_latest_by_natural_key</c> are two of the nine identically-named facts, but the
/// names are coincidence: Marten registers <c>LiveStreamAggregation&lt;T&gt;()</c>, while Polecat's
/// same-named tests register the projection <c>Inline</c> with a comment saying the inline
/// registration is what creates the lookup at all. The two stores disagree about whether a
/// live-only registration populates a natural key index, so the shared precondition does not exist
/// yet. Lifecycle equivalence itself is already
/// <see cref="SnapshotLifecycleCompliance{TFixture,TOperations,TQuerySession}"/>'s job.
/// </description></item>
/// </list>
/// <para>
/// <b>Uniqueness across streams was the one open question, and it is now settled</b> (jasperfx#764).
/// Fisher refused a second stream claiming a live key while Polecat's <c>MERGE</c> repointed it at
/// the newcomer; refusing is the contract, and
/// <see cref="a_second_stream_cannot_claim_a_live_natural_key" /> pins it — with the shared
/// <see cref="DuplicateNaturalKeyException" /> and, more importantly, with the original mapping read
/// back afterwards. Polecat carries the behavioral change (polecat#549).
/// </para>
/// <para>
/// <b>The load-bearing facts are the rebuild pair</b> (marten#4788 / marten#4966 / polecat#259).
/// The natural key lookup is maintained by the inline append path, which drives off newly-appended
/// stream actions; a daemon rebuild replays persisted events without appending streams, so the
/// table was simply never repopulated and every natural key fetch missed after a rebuild. Both
/// rebuild facts here register the snapshot <see cref="SnapshotLifecycle.Async"/> rather than
/// Inline, and that is deliberate: under an async registration the inline path never runs, so the
/// lookup can only have been written by the daemon. The obvious alternative — append inline, then
/// rebuild, then fetch — passes vacuously on any store whose rebuild does not tear the lookup down,
/// which is the same reasoning that makes
/// <see cref="AggregateWriteCacheCompliance{TFixture,TOperations,TQuerySession}"/> count cache hits
/// rather than trust its correctness facts.
/// </para>
/// </remarks>
public abstract class NaturalKeyCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static void addEventTypes(ComplianceStoreConfig config)
    {
        config.AddEventType<NaturalKeyOrderPlaced>();
        config.AddEventType<NaturalKeyOrderItemAdded>();
        config.AddEventType<NaturalKeyOrderRenumbered>();
        config.AddEventType<NaturalKeyOrderCompleted>();
    }

    /// <summary>
    /// The standard configuration: Guid stream identity, wrapped natural key, inline snapshot.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_natural_key";

        addEventTypes(config);
        config.AddEventType<NaturalKeyInvoiceRaised>();

        config.RegisterValueType<ComplianceOrderNumber>();
        config.Snapshot<ComplianceNaturalKeyOrder>(SnapshotLifecycle.Inline);
        config.Snapshot<ComplianceNaturalKeyInvoice>(SnapshotLifecycle.Inline);
    };

    /// <summary>
    /// String stream identity. Its own store, because stream identity is store-level.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_natural_key_string";
        config.StreamIdentity = StreamIdentity.AsString;

        addEventTypes(config);

        config.RegisterValueType<ComplianceOrderNumber>();
        config.Snapshot<ComplianceNaturalKeyOrderByKey>(SnapshotLifecycle.Inline);
    };

    /// <summary>
    /// The rebuild configuration. Async rather than Inline so the daemon is the only thing that can
    /// ever have written a lookup row — see the class remarks on vacuity.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _asyncConfiguration = config =>
    {
        config.SchemaName = "compliance_natural_key_async";

        addEventTypes(config);

        config.RegisterValueType<ComplianceOrderNumber>();
        config.Snapshot<ComplianceNaturalKeyOrder>(SnapshotLifecycle.Async);
    };

    /// <summary>
    /// Conjoined tenancy, for the isolation fact. All three products carry a tenancy-plus-natural-key
    /// test file, which is the strongest shared signal in the cluster.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _tenantedConfiguration = config =>
    {
        config.SchemaName = "compliance_natural_key_tenanted";
        config.ConjoinedEventTenancy = true;

        addEventTypes(config);

        config.RegisterValueType<ComplianceOrderNumber>();
        config.Snapshot<ComplianceNaturalKeyOrder>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    /// <summary>
    /// Capability gate. xunit v3's declarative <c>SkipUnless</c> needs a static property, which
    /// cannot reach the fixture instance, so every fact opens with this call instead.
    /// </summary>
    private void SkipUnlessNaturalKeysAreSupported()
    {
        Assert.SkipUnless(theFixture.SupportsNaturalKeys,
            "This event store does not implement natural key lookups for [NaturalKey] aggregates");
    }

    /// <summary>
    /// Skips configuration while the gate is closed: a store with no natural key support may fail
    /// while <em>building</em> a store whose aggregates declare one, which would take the whole
    /// suite down instead of skipping it.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync();

        if (!theFixture.SupportsNaturalKeys)
        {
            return;
        }

        await theFixture.ConfigureAsync(Configuration);
        await theFixture.CleanEventDataAsync();
    }

    private async Task useConfigurationAsync(Action<ComplianceStoreConfig> configuration)
    {
        await theFixture.ConfigureAsync(configuration);
        await theFixture.CleanEventDataAsync();
    }

    private async Task<Guid> anOrderAsync(ComplianceOrderNumber number, params object[] more)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream<ComplianceNaturalKeyOrder>(streamId,
            new object[] { new NaturalKeyOrderPlaced(number, "Alice") }.Concat(more).ToArray());
        await SaveChangesAsync(session);

        return streamId;
    }

    private static ComplianceOrderNumber aNumber(string value) => new($"{value}-{Guid.NewGuid():N}");

    // ---------- the round trip ----------

    [Fact]
    public async Task fetch_for_writing_existing_stream_by_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var number = aNumber("ORD");
        var streamId = await anOrderAsync(number, new NaturalKeyOrderItemAdded("Widget", 9.99m));

        await using var session = OpenSession();
        var stream = await EventsFor(session)
            .FetchForWriting<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Number.ShouldBe(number);
        stream.Aggregate.Customer.ShouldBe("Alice");

        // The handle resolved by natural key is a handle on the *stream*, so it knows the stream id.
        stream.Id.ShouldBe(streamId);
    }

    [Fact]
    public async Task fetch_for_writing_and_append_events_by_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var number = aNumber("ORD");
        await anOrderAsync(number);

        await using (var session = OpenSession())
        {
            var stream = await EventsFor(session)
                .FetchForWriting<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

            stream.AppendOne(new NaturalKeyOrderItemAdded("Gadget", 19.99m));
            stream.AppendOne(new NaturalKeyOrderItemAdded("Doohickey", 5.50m));
            stream.AppendOne(new NaturalKeyOrderCompleted());
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var order = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

        order.ShouldNotBeNull();
        order.Total.ShouldBe(25.49m);
        order.Complete.ShouldBeTrue();
    }

    [Fact]
    public async Task fetch_latest_by_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var number = aNumber("ORD");
        await anOrderAsync(number, new NaturalKeyOrderItemAdded("Thingamajig", 15.00m));

        await using var session = OpenSession();
        var order = await EventsFor(session)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

        order.ShouldNotBeNull();
        order.Number.ShouldBe(number);
        order.Customer.ShouldBe("Alice");
        order.Total.ShouldBe(15.00m);
    }

    /// <summary>
    /// The one miss behavior Marten and Polecat agree on, and therefore the only one this suite
    /// asserts. See the class remarks for why the <c>FetchForWriting</c> miss is excluded.
    /// </summary>
    [Fact]
    public async Task fetch_latest_returns_null_for_nonexistent_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        await using var session = OpenSession();
        var order = await EventsFor(session)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(aNumber("ORD-NOPE"), Cancellation);

        order.ShouldBeNull();
    }

    [Fact]
    public async Task fetch_for_exclusive_writing_by_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var number = aNumber("ORD");
        var streamId = await anOrderAsync(number, new NaturalKeyOrderItemAdded("Contraption", 42.00m));

        await using (var session = OpenSession())
        {
            var stream = await EventsFor(session)
                .FetchForExclusiveWriting<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

            stream.Aggregate.ShouldNotBeNull();
            stream.Aggregate.Number.ShouldBe(number);
            stream.Aggregate.Total.ShouldBe(42.00m);
            stream.Id.ShouldBe(streamId);

            stream.AppendOne(new NaturalKeyOrderCompleted());
            await SaveChangesAsync(session);
        }

        // The exclusive lock has to be released by disposing the session, or this read blocks.
        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(3);
    }

    [Fact]
    public async Task natural_key_with_a_primitive_string_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var code = $"INV-{Guid.NewGuid():N}";
        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceNaturalKeyInvoice>(streamId,
                new NaturalKeyInvoiceRaised(code, 250.00m));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();

        var stream = await EventsFor(query)
            .FetchForWriting<ComplianceNaturalKeyInvoice, string>(code, Cancellation);
        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Amount.ShouldBe(250.00m);
        stream.Id.ShouldBe(streamId);

        var latest = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyInvoice, string>(code, Cancellation);
        latest.ShouldNotBeNull();
        latest.Code.ShouldBe(code);
    }

    // ---------- mutability ----------

    /// <summary>
    /// The property that distinguishes a natural key from a stream id: it can change, and the lookup
    /// has to follow it.
    /// </summary>
    [Fact]
    public async Task natural_key_is_mutable_fetch_after_change()
    {
        SkipUnlessNaturalKeysAreSupported();

        var original = aNumber("ORD-OLD");
        var renumbered = aNumber("ORD-NEW");

        var streamId = await anOrderAsync(original);

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new NaturalKeyOrderRenumbered(renumbered));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var stream = await EventsFor(query)
            .FetchForWriting<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(renumbered, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Number.ShouldBe(renumbered);
        stream.Aggregate.Customer.ShouldBe("Alice");
        stream.Id.ShouldBe(streamId);
    }

    /// <summary>
    /// polecat#435 / marten#5041: a stream has exactly one <em>current</em> natural key, so renaming
    /// retires the previous one.
    /// </summary>
    /// <remarks>
    /// Both products originally asserted the opposite — that the superseded key still resolved,
    /// because the old row was left behind — and both reframed it as a defect rather than a feature:
    /// a retired alias that resolves forever also occupies its slot in the lookup's primary key
    /// forever, so no other stream can ever claim that identifier. Asserted through
    /// <c>FetchLatest</c> because it is the one miss the products spell the same way.
    /// </remarks>
    [Fact]
    public async Task renaming_the_natural_key_retires_the_previous_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var original = aNumber("ORD-OLD");
        var renumbered = aNumber("ORD-NEW");

        var streamId = await anOrderAsync(original);

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new NaturalKeyOrderRenumbered(renumbered));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();

        (await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(renumbered, Cancellation))
            .ShouldNotBeNull();

        (await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(original, Cancellation))
            .ShouldBeNull();
    }

    // ---------- uniqueness ----------

    /// <summary>
    /// jasperfx#764: a natural key already mapped to a live stream is <em>refused</em> to a second
    /// claimant, and the original mapping survives the attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was the one open product question in the cluster, and it is settled: refusing is the
    /// contract. Fisher refuses with <c>DuplicateNaturalKeyException</c>; Polecat's <c>MERGE</c>
    /// lookup write repointed the key at the newcomer, which leaves the original stream in place but
    /// unreachable by the identifier it was created with, and reports nothing. A natural key exists
    /// to name one stream, so a second claimant is a bug in the caller's key derivation rather than
    /// an instruction — and of the two failure modes, the silent one is worse. Polecat carries the
    /// behavioral change (polecat#549).
    /// </para>
    /// <para>
    /// <b>The second half is the half that does the work.</b> Asserting only the throw would pass on
    /// a store that repoints the row and <em>then</em> fails the transaction, or that writes the new
    /// mapping through a path the failure does not roll back — the mapping is the thing being
    /// protected, so it has to be read back. It is checked through <c>FetchLatest</c>, the one miss
    /// the products spell identically, and by stream id rather than by nullness so a store that
    /// repointed the key still fails here rather than passing on "something resolves".
    /// </para>
    /// <para>
    /// Distinct from the two neighbouring behaviors that are <em>not</em> duplicates and are pinned
    /// elsewhere: re-asserting the same mapping on its own stream is idempotent by design, and
    /// renaming a key retires the superseded one — see
    /// <see cref="renaming_the_natural_key_retires_the_previous_key" />, which is what frees an
    /// identifier for a later stream to claim legitimately.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task a_second_stream_cannot_claim_a_live_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var number = aNumber("ORD-DUP");
        var originalId = await anOrderAsync(number, new NaturalKeyOrderItemAdded("Widget", 9.99m));

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceNaturalKeyOrder>(Guid.NewGuid(),
                new NaturalKeyOrderPlaced(number, "Mallory"));

            var ex = await Should.ThrowAsync<DuplicateNaturalKeyException>(() => SaveChangesAsync(session));

            // Unwrapped, not the ComplianceOrderNumber wrapper: NaturalKeyDefinition unwraps before
            // the lookup is written, so the value in the row — and therefore at the throw site — is
            // the inner string.
            ex.Key.ShouldBe(number.Value);
            ex.AggregateType.ShouldBe(typeof(ComplianceNaturalKeyOrder));
        }

        // The mapping the refusal exists to protect. A store that throws but has already repointed
        // the row fails here, which is the whole point of reading it back.
        await using var query = OpenSession();
        var order = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

        order.ShouldNotBeNull();
        order.Id.ShouldBe(originalId);
        order.Customer.ShouldBe("Alice");
        order.Total.ShouldBe(9.99m);
    }

    // ---------- archiving and cleaning ----------

    /// <summary>
    /// An archived stream drops out of the lookup. Evidenced in Fisher (which resolves it by joining
    /// the streams table rather than copying the flag) and in Polecat's rebuild test, which asserts
    /// the archived stream's row does not come back.
    /// </summary>
    [Fact]
    public async Task an_archived_stream_no_longer_resolves_by_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        var number = aNumber("ORD-ARCHIVED");
        var streamId = await anOrderAsync(number);

        await using (var session = OpenSession())
        {
            EventsFor(session).ArchiveStream(streamId);
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var order = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

        order.ShouldBeNull();
    }

    /// <summary>
    /// The lookup rows go with the event data they describe.
    /// </summary>
    /// <remarks>
    /// Lifted from Fisher, whose test comment names precisely why it belongs in <em>this</em>
    /// library rather than only in a product: the compliance fixture cleans event data before every
    /// test, so a store that leaves lookup rows behind presents as an unexplained failure two tests
    /// later rather than as a clean failure here.
    /// </remarks>
    [Fact]
    public async Task cleaning_the_event_data_clears_the_natural_key_lookup()
    {
        SkipUnlessNaturalKeysAreSupported();

        var number = aNumber("ORD-CLEANED");
        await anOrderAsync(number);

        await theFixture.CleanEventDataAsync();

        await using (var query = OpenSession())
        {
            (await EventsFor(query)
                .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation))
                .ShouldBeNull();
        }

        // And the identifier is free again, which is the half a stale row would break.
        var streamId = await anOrderAsync(number);

        await using var check = OpenSession();
        var order = await EventsFor(check)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

        order.ShouldNotBeNull();
        order.Id.ShouldBe(streamId);
    }

    // ---------- string stream identity ----------

    [Fact]
    public async Task string_identity_fetch_for_writing_by_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        await useConfigurationAsync(_stringConfiguration);

        var number = aNumber("ORD-STR");
        var streamKey = $"order/{Guid.NewGuid():N}";

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceNaturalKeyOrderByKey>(streamKey,
                new NaturalKeyOrderPlaced(number, "Iris"),
                new NaturalKeyOrderItemAdded("Lever", 12.50m));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var stream = await EventsFor(query)
            .FetchForWriting<ComplianceNaturalKeyOrderByKey, ComplianceOrderNumber>(number, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Number.ShouldBe(number);
        stream.Aggregate.Customer.ShouldBe("Iris");
        stream.Aggregate.Total.ShouldBe(12.50m);
        stream.Key.ShouldBe(streamKey);
    }

    [Fact]
    public async Task string_identity_fetch_latest_by_natural_key()
    {
        SkipUnlessNaturalKeysAreSupported();

        await useConfigurationAsync(_stringConfiguration);

        var number = aNumber("ORD-STR");
        var streamKey = $"order/{Guid.NewGuid():N}";

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream<ComplianceNaturalKeyOrderByKey>(streamKey,
                new NaturalKeyOrderPlaced(number, "Jack"),
                new NaturalKeyOrderItemAdded("Pulley", 8.00m));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var order = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrderByKey, ComplianceOrderNumber>(number, Cancellation);

        order.ShouldNotBeNull();
        order.Number.ShouldBe(number);
        order.Customer.ShouldBe("Jack");
        order.Total.ShouldBe(8.00m);
    }

    // ---------- tenancy ----------

    /// <summary>
    /// Under conjoined tenancy the lookup is keyed on (tenant, key), so one business identifier may
    /// exist once per tenant and must resolve to that tenant's stream in both directions.
    /// </summary>
    /// <remarks>
    /// The strongest shared signal in the cluster — Marten, Polecat and Fisher each carry a
    /// tenancy-plus-natural-key test file. Both directions are checked for the reason
    /// <see cref="ConjoinedEventTenancyCompliance{TFixture,TOperations,TQuerySession}"/> gives: a
    /// store that leaks across tenants still answers correctly for whichever tenant owns the row.
    /// </remarks>
    [Fact]
    public async Task the_natural_key_lookup_is_isolated_by_tenant()
    {
        SkipUnlessNaturalKeysAreSupported();

        Assert.SkipUnless(theFixture.SupportsConjoinedEventTenancy,
            "This event store cannot slice one database by tenant");

        await useConfigurationAsync(_tenantedConfiguration);

        var shared = aNumber("ORD-SHARED");

        var acme = await anOrderForTenantAsync("acme", shared);
        var globex = await anOrderForTenantAsync("globex", shared);

        acme.ShouldNotBe(globex);

        await using (var session = await openForTenantAsync("acme"))
        {
            var order = await EventsFor(session)
                .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(shared, Cancellation);
            order.ShouldNotBeNull();
            order.Id.ShouldBe(acme);
        }

        await using var other = await openForTenantAsync("globex");
        var theirs = await EventsFor(other)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(shared, Cancellation);
        theirs.ShouldNotBeNull();
        theirs.Id.ShouldBe(globex);
    }

    /// <summary>
    /// A session bound to one tenant, through the shared generic store surface — the same route
    /// <see cref="ConjoinedEventTenancyCompliance{TFixture,TOperations,TQuerySession}"/> takes.
    /// </summary>
    private async Task<TOperations> openForTenantAsync(string tenantId)
    {
        var store = (IEventStore<TOperations, TQuerySession>)theFixture.EventStore;

        var databases = await theFixture.EventStore.AllDatabases();

        return store.OpenSession(databases.First(), tenantId);
    }

    private async Task<Guid> anOrderForTenantAsync(string tenantId, ComplianceOrderNumber number)
    {
        var streamId = Guid.NewGuid();

        await using var session = await openForTenantAsync(tenantId);
        EventsFor(session).StartStream<ComplianceNaturalKeyOrder>(streamId,
            new NaturalKeyOrderPlaced(number, tenantId));
        await SaveChangesAsync(session);

        return streamId;
    }

    // ---------- rebuild: the load-bearing facts ----------

    /// <summary>
    /// marten#4788 / polecat#259: the natural key lookup must be maintained by the async projection
    /// path, not only by the inline append path.
    /// </summary>
    /// <remarks>
    /// Registered <see cref="SnapshotLifecycle.Async"/>, so nothing but the daemon can ever have
    /// written a lookup row — which is what keeps the fact from passing vacuously on a store whose
    /// rebuild leaves the table untouched. The products' own versions of this test reach for raw SQL
    /// to delete the table first and then count rows; the async registration gets the same
    /// guarantee through the shared surface, since the lookup table's name is per-product and its
    /// contents are unreachable from anything portable.
    /// </remarks>
    [Fact]
    public async Task natural_key_resolves_after_a_projection_rebuild()
    {
        SkipUnlessNaturalKeysAreSupported();

        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await useConfigurationAsync(_asyncConfiguration);

        var number = aNumber("ORD-REBUILD");
        var streamId = await anOrderAsync(number, new NaturalKeyOrderItemAdded("Widget", 11.00m));

        var daemon = await StartDaemonAsync();
        await daemon.RebuildProjectionAsync<ComplianceNaturalKeyOrder>(Cancellation);

        await using var query = OpenSession();
        var order = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(number, Cancellation);

        order.ShouldNotBeNull();
        order.Id.ShouldBe(streamId);
        order.Number.ShouldBe(number);
        order.Total.ShouldBe(11.00m);
    }

    /// <summary>
    /// marten#4966: a rebuild has to land on the stream's <em>current</em> key, not the one it was
    /// created with — the rebuild replays both events, so a store that emits a lookup row per
    /// key-carrying event without retiring the superseded one resolves the wrong key, or both.
    /// </summary>
    [Fact]
    public async Task a_renamed_natural_key_resolves_after_a_projection_rebuild()
    {
        SkipUnlessNaturalKeysAreSupported();

        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await useConfigurationAsync(_asyncConfiguration);

        var original = aNumber("ORD-OLD");
        var renumbered = aNumber("ORD-NEW");

        var streamId = await anOrderAsync(original);

        await using (var session = OpenSession())
        {
            EventsFor(session).Append(streamId, new NaturalKeyOrderRenumbered(renumbered));
            await SaveChangesAsync(session);
        }

        var daemon = await StartDaemonAsync();
        await daemon.RebuildProjectionAsync<ComplianceNaturalKeyOrder>(Cancellation);

        await using var query = OpenSession();

        var order = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(renumbered, Cancellation);
        order.ShouldNotBeNull();
        order.Id.ShouldBe(streamId);
        order.Number.ShouldBe(renumbered);

        (await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(original, Cancellation))
            .ShouldBeNull();
    }

    /// <summary>
    /// A rebuild must not resurrect an archived stream's natural key. Lifted from polecat#259's
    /// third fact, whose reasoning is that rebuild only ever inserts, so a surviving archived row
    /// could only mean teardown failed to wipe the lookup.
    /// </summary>
    [Fact]
    public async Task an_archived_stream_does_not_come_back_on_rebuild()
    {
        SkipUnlessNaturalKeysAreSupported();

        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        await useConfigurationAsync(_asyncConfiguration);

        var activeNumber = aNumber("ORD-ACTIVE");
        var archivedNumber = aNumber("ORD-ARCHIVED");

        var activeId = await anOrderAsync(activeNumber);
        var archivedId = await anOrderAsync(archivedNumber);

        await using (var session = OpenSession())
        {
            EventsFor(session).ArchiveStream(archivedId);
            await SaveChangesAsync(session);
        }

        var daemon = await StartDaemonAsync();
        await daemon.RebuildProjectionAsync<ComplianceNaturalKeyOrder>(Cancellation);

        await using var query = OpenSession();

        var active = await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(activeNumber, Cancellation);
        active.ShouldNotBeNull();
        active.Id.ShouldBe(activeId);

        (await EventsFor(query)
            .FetchLatest<ComplianceNaturalKeyOrder, ComplianceOrderNumber>(archivedNumber, Cancellation))
            .ShouldBeNull();
    }
}
