using System;
using System.Text.Json;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using JasperFx.Events.Upcasting;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Upcasting events and aggregate

/// <summary>
/// The v1 event schema — what an application appended before the migration. Only the "legacy"
/// store configuration knows these types; the upcasting configuration deliberately does not,
/// because the whole point of an upcast is that application code drops the old type.
/// </summary>
public record UpcastCartOpened(Guid CartId, Guid ClientId);

public record UpcastCouponClipped(Guid CartId, int Percent);

public record UpcastGiftNoted(Guid CartId, string Note);

/// <summary>
/// The v2 event schema — what aggregations and projections are written against after the
/// migration.
/// </summary>
public record UpcastCartInitialized(Guid CartId, Guid ClientId, string Status);

public record UpcastDiscountApplied(Guid CartId, double Fraction);

public record UpcastGiftRecorded(Guid CartId, string Note, int Length);

/// <summary>
/// Folds exclusively over the NEW event types. That is the property upcasting exists to provide:
/// once a transformation is registered, no read path — stream fetch, live aggregation,
/// FetchForWriting, the daemon — ever hands application code the old schema.
/// </summary>
public partial class UpcastCartSummary
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Status { get; set; } = string.Empty;
    public double Discount { get; set; }
    public int GiftCount { get; set; }

    public static UpcastCartSummary Create(UpcastCartInitialized e) =>
        new() { ClientId = e.ClientId, Status = e.Status };

    public void Apply(UpcastDiscountApplied e) => Discount = e.Fraction;

    public void Apply(UpcastGiftRecorded e) => GiftCount++;
}

#endregion

/// <summary>
/// The shared event upcasting contract (jasperfx#752): transformations registered against
/// <c>EventRegistry.Upcasters</c> reinterpret old stored event schemas as the current CLR event
/// types on every read path.
/// </summary>
/// <remarks>
/// <para>
/// Gated on <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.SupportsUpcasting"/>,
/// which defaults to FALSE — the one gate that ships closed, because the contract is being defined
/// ahead of any store implementing it. A store enrolls, implements
/// <c>IComplianceStoreRegistrar.Upcast</c> and its own <c>IUpcastPayload</c> adapter, then flips
/// the gate and makes the facts pass. The gate also short-circuits store configuration in
/// <see cref="InitializeAsync"/>, so an enrolled-but-unimplemented store never reaches the
/// registrar member's throwing default.
/// </para>
/// <para>
/// Most facts run in two phases against one schema: a "legacy" store configuration (old event
/// types, no upcasts) writes the rows, then the standard configuration (upcasts, no old types)
/// reads them back. That is the honest reproduction of the migration story — the old rows really
/// were written by a store that had never heard of the transformation.
/// </para>
/// <para>
/// The exception is <see cref="a_typed_append_of_the_old_event_type_does_not_shadow_the_upcaster"/>,
/// which pins the marten#4680 semantics inside a single store: a registered transformation is the
/// authoritative interpretation of its source event type name, and a typed append of the old CLR
/// type — which records a stored-CLR-type hint alongside the name in stores that keep one — must
/// not shadow it on read.
/// </para>
/// </remarks>
public abstract class UpcastingCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly string _couponEventTypeName =
        EventTypeExtensions.GetEventTypeName<UpcastCouponClipped>();

    /// <summary>
    /// The store as it existed before the migration: old event types, no upcasts.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _legacyConfiguration = config =>
    {
        config.SchemaName = "compliance_upcasting";

        config.AddEventType<UpcastCartOpened>();
        config.AddEventType<UpcastCouponClipped>();
        config.AddEventType<UpcastGiftNoted>();
    };

    /// <summary>
    /// The store after the migration. One transformation per registration shape: typed sync, raw
    /// <see cref="JsonDocument"/> (no old CLR type involved), and async-only typed.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_upcasting";

        config.Upcast(UpcastTransformation.For<UpcastCartOpened, UpcastCartInitialized>(
            old => new UpcastCartInitialized(old.CartId, old.ClientId, "Opened")));

        config.Upcast(UpcastTransformation.FromJson(
            document =>
            {
                var root = document.RootElement;
                return new UpcastDiscountApplied(
                    root.GetProperty("CartId").GetGuid(),
                    root.GetProperty("Percent").GetInt32() / 100.0);
            },
            _couponEventTypeName));

        config.Upcast(UpcastTransformation.For<UpcastGiftNoted, UpcastGiftRecorded>(
            (old, _) => Task.FromResult(new UpcastGiftRecorded(old.CartId, old.Note, old.Note.Length))));

        config.Snapshot<UpcastCartSummary>(SnapshotLifecycle.Async);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Capability gate. xunit v3's declarative SkipUnless needs a static property, which cannot
    /// consult a per-store fixture instance, so the gate runs as a dynamic skip instead.
    /// </summary>
    private void SkipUnlessUpcastingIsSupported()
    {
        Assert.SkipUnless(theFixture.SupportsUpcasting,
            "This event store has not implemented the shared event upcasting contract");
    }

    /// <summary>
    /// Skips configuration entirely while the gate is closed — the registrar's <c>Upcast</c>
    /// member has a throwing default, and reaching it from an unimplemented store would turn every
    /// skip into a failure.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync().ConfigureAwait(false);

        if (!theFixture.SupportsUpcasting)
        {
            return;
        }

        await theFixture.ConfigureAsync(Configuration).ConfigureAwait(false);
        await theFixture.CleanEventDataAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Write rows the way the pre-migration application really wrote them: through a store built
    /// from <see cref="_legacyConfiguration"/>, then hand the schema back to the upcasting store.
    /// </summary>
    private async Task<Guid> appendLegacyEventsAsync(params object[] events)
    {
        await theFixture.ConfigureAsync(_legacyConfiguration);

        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId, events);
            await SaveChangesAsync(session);
        }

        await theFixture.ConfigureAsync(_configuration);

        return streamId;
    }

    [Fact]
    public async Task an_event_stored_under_the_old_name_deserializes_as_the_new_type()
    {
        SkipUnlessUpcastingIsSupported();

        var clientId = Guid.NewGuid();
        var streamId = await appendLegacyEventsAsync(new UpcastCartOpened(Guid.NewGuid(), clientId));

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        var initialized = events.ShouldHaveSingleItem().Data.ShouldBeOfType<UpcastCartInitialized>();
        initialized.ClientId.ShouldBe(clientId);
        initialized.Status.ShouldBe("Opened");
    }

    [Fact]
    public async Task a_raw_json_transformation_upcasts_without_the_old_clr_type()
    {
        SkipUnlessUpcastingIsSupported();

        var streamId = await appendLegacyEventsAsync(new UpcastCouponClipped(Guid.NewGuid(), 25));

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        // The transformation only ever saw the stored JSON — UpcastCouponClipped as a CLR type
        // plays no part in the upcasting store's configuration.
        events.ShouldHaveSingleItem().Data.ShouldBeOfType<UpcastDiscountApplied>()
            .Fraction.ShouldBe(0.25);
    }

    [Fact]
    public async Task an_async_only_upcast_applies_on_the_async_read_path()
    {
        SkipUnlessUpcastingIsSupported();

        var streamId = await appendLegacyEventsAsync(new UpcastGiftNoted(Guid.NewGuid(), "happy birthday"));

        // FetchStreamAsync is the asynchronous read path, so the contract requires the async
        // transformation to run here. A store routing its async reads through the synchronous
        // delegate fails with UpcastingException instead — by design.
        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        var recorded = events.ShouldHaveSingleItem().Data.ShouldBeOfType<UpcastGiftRecorded>();
        recorded.Note.ShouldBe("happy birthday");
        recorded.Length.ShouldBe("happy birthday".Length);
    }

    [Fact]
    public async Task upcasting_applies_in_live_aggregation()
    {
        SkipUnlessUpcastingIsSupported();

        var clientId = Guid.NewGuid();
        var streamId = await appendLegacyEventsAsync(
            new UpcastCartOpened(Guid.NewGuid(), clientId),
            new UpcastCouponClipped(Guid.NewGuid(), 40));

        await using var session = OpenSession();
        var summary = await EventsFor(session)
            .AggregateStreamAsync<UpcastCartSummary>(streamId, token: Cancellation);

        // The aggregate has no Apply for any v1 type, so folding succeeds only if every event was
        // upcast before the aggregation saw it.
        summary.ShouldNotBeNull();
        summary.ClientId.ShouldBe(clientId);
        summary.Status.ShouldBe("Opened");
        summary.Discount.ShouldBe(0.4);
    }

    [Fact]
    public async Task upcasting_applies_in_fetch_for_writing()
    {
        SkipUnlessUpcastingIsSupported();

        var streamId = await appendLegacyEventsAsync(new UpcastCartOpened(Guid.NewGuid(), Guid.NewGuid()));

        await using (var session = OpenSession())
        {
            var stream = await EventsFor(session).FetchForWriting<UpcastCartSummary>(streamId, Cancellation);

            stream.Aggregate.ShouldNotBeNull();
            stream.Aggregate.Status.ShouldBe("Opened");

            // The write handle keeps working over an upcast stream: new-schema events append
            // against the version the old-schema rows established.
            stream.AppendOne(new UpcastDiscountApplied(streamId, 0.1));
            await SaveChangesAsync(session);
        }

        await using var verify = OpenSession();
        var summary = await EventsFor(verify)
            .AggregateStreamAsync<UpcastCartSummary>(streamId, token: Cancellation);

        summary.ShouldNotBeNull();
        summary.Discount.ShouldBe(0.1);
    }

    [Fact]
    public async Task upcasting_applies_in_async_daemon_projections()
    {
        SkipUnlessUpcastingIsSupported();
        Assert.SkipUnless(theFixture.SupportsAsyncDaemon,
            "This event store does not support the async projection daemon under test");

        var clientId = Guid.NewGuid();
        var streamId = await appendLegacyEventsAsync(
            new UpcastCartOpened(Guid.NewGuid(), clientId),
            new UpcastGiftNoted(Guid.NewGuid(), "bow"),
            new UpcastCouponClipped(Guid.NewGuid(), 15));

        await StartDaemonAsync();
        await WaitForNonStaleProjectionDataAsync(_timeout);

        await using var query = OpenSession();
        var summary = await LoadDocumentAsync<UpcastCartSummary>(query, streamId);

        summary.ShouldNotBeNull();
        summary.ClientId.ShouldBe(clientId);
        summary.GiftCount.ShouldBe(1);
        summary.Discount.ShouldBe(0.15);
    }

    [Fact]
    public async Task a_typed_append_of_the_old_event_type_does_not_shadow_the_upcaster()
    {
        SkipUnlessUpcastingIsSupported();

        // marten#4680, in one store: the old CLR type is appended TYPED into the store that
        // carries the upcaster, so the row records both the source event type name and — in stores
        // that keep one — a stored-CLR-type hint pointing at UpcastCartOpened. The registered
        // transformation is the authoritative interpretation of that name, so the read must still
        // upcast; a store that lets the hint pick the old CLR type back has the exact bug the
        // issue documented.
        var clientId = Guid.NewGuid();
        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId, new UpcastCartOpened(Guid.NewGuid(), clientId));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.ShouldHaveSingleItem().Data.ShouldBeOfType<UpcastCartInitialized>()
            .ClientId.ShouldBe(clientId);
    }
}
