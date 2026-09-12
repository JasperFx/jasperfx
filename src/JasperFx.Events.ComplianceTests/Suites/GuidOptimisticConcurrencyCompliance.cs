using System;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using JasperFx.Metadata;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <see cref="Guid" /> optimistic concurrency for a document declared with
/// <see cref="IVersioned" /> — the guard that lets a document loaded in one request be written back
/// in another, and refuses the write when someone else got there first (jasperfx#819 §1).
/// </summary>
/// <remarks>
/// <para>
/// <b>There was no shared suite for this, on any store.</b> The whole shared coverage of document
/// concurrency was <see cref="NumericRevisionCompliance{TFixture}" />; every other "optimistic" fact
/// in the suite set is about the <em>event</em> store — <c>AppendOptimistic</c> in
/// <c>FetchForWritingCompliance</c> and <c>StringStreamIdentityCompliance</c>,
/// <c>FetchForWritingByTags</c> in the DCB suite. Grepping the 2.68.0 suites for
/// <see cref="IVersioned" /> returned nothing at all.
/// </para>
/// <para>
/// <b>What that cost.</b> fisher#245: Fisher's Guid optimistic concurrency worked only <em>inside one
/// session</em>. The guard was fed from the session's own version tracker — what that session had
/// read — so a document loaded in one session and stored through another had no recorded expectation
/// and failed its guard every time. It refused stale writes correctly and refused legitimate ones
/// too, which is the workflow the feature exists for:
/// </para>
/// <code>
/// Order order;
/// await using (var session = store.LightweightSession())
///     order = await session.LoadAsync&lt;Order&gt;(id);
///
/// order.Status = "shipped";
/// await using (var session = store.LightweightSession())
/// {
///     session.Store(order);           // ConcurrencyException, every time, on an unmodified row
///     await session.SaveChangesAsync();
/// }
/// </code>
/// <para>
/// Fisher passed all fifty enrolled suites throughout. It is also not a Fisher-only shape:
/// marten#5372 is the same field one route over — a member mapped with
/// <c>Metadata.Version.MapTo(...)</c> was invisible to the session, so the upsert bound
/// <c>DBNull</c> into its guard and reported <em>every</em> cross-session write as a violation. Two
/// stores, the same field, the same failure mode, found independently.
/// </para>
/// <para>
/// <b>The pairing is the design.</b> Each "it works" fact has an "and staleness is still refused"
/// twin, because the cheap wrong fix — seed nothing, or seed whatever the row currently holds —
/// makes one half pass and the other fail. A suite carrying only the first half would have blessed
/// exactly that fix.
/// </para>
/// <para>
/// <b>Declared twice.</b> <see cref="ComplianceShipment" /> implements <see cref="IVersioned" /> and
/// the configuration also calls
/// <see cref="DocumentComplianceConfig.UseOptimisticConcurrency{T}" />, because the stores disagree
/// about whether the marker is itself the opt-in or merely supplies the member to guard on. Saying
/// both leaves this suite testing the concurrency behavior rather than that disagreement.
/// </para>
/// <para>
/// <b>Scoping note.</b> <c>Update&lt;T&gt;</c> is not on the document contract —
/// <see cref="IDocumentWriteOperations" /> is <c>Store</c>, <c>Delete</c> and <c>DeleteWhere</c> —
/// so "Update behaves as Store does across a session boundary" stays product-tested, exactly as
/// jasperfx#785 §5.3 keeps <c>UpdateRevision</c> off the numeric side. Everything below runs through
/// <see cref="IDocumentWriteOperations.Store{T}" />, <c>LoadAsync</c> and
/// <see cref="IVersioned.Version" />.
/// </para>
/// </remarks>
public abstract class GuidOptimisticConcurrencyCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_concurrency";
        config.AddDocumentType<ComplianceShipment>();
        config.UseOptimisticConcurrency<ComplianceShipment>();

        // The control. A type with no version member shares the store and must be untouched by any
        // of this.
        config.AddDocumentType<ComplianceWidget>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsOptimisticConcurrency,
            "This document store does not implement Guid optimistic concurrency");
    }

    /// <summary>
    /// Store one document, committing in its own session — one session per write throughout, which
    /// is the whole point of the suite.
    /// </summary>
    private async Task StoreAsync(ComplianceShipment shipment)
    {
        await using var session = LightweightSession();
        session.Store(shipment);
        await session.SaveChangesAsync(Cancellation);
    }

    private async Task<ConcurrencyException> StoreShouldBeRefusedAsync(ComplianceShipment shipment)
        => await Should.ThrowAsync<ConcurrencyException>(() => StoreAsync(shipment));

    /// <summary>
    /// Load through a session of its own, so the returned instance carries no session affinity. A
    /// helper rather than an inline call because "which session loaded it" is the variable under
    /// test and every load in this suite has to be uniform about it.
    /// </summary>
    private async Task<ComplianceShipment> LoadAsync(Guid id)
    {
        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceShipment>(id, Cancellation);
        loaded.ShouldNotBeNull();
        return loaded;
    }

    private async Task<Guid> AShipmentAsync(string supplier = "Acme", string status = "draft")
    {
        var id = Guid.NewGuid();
        await StoreAsync(new ComplianceShipment { Id = id, Supplier = supplier, Status = status });
        return id;
    }

    /// <summary>
    /// The baseline: a versioned document can be created at all. The guard does not turn every insert
    /// into a failure, and the store stamps a version rather than leaving the default.
    /// </summary>
    /// <remarks>
    /// Stated first because every fact below assumes it, and because it is the one a guard fed from
    /// the wrong place can still pass — which is what makes the rest of the suite necessary.
    /// </remarks>
    [Fact]
    public async Task a_versioned_document_can_be_created()
    {
        SkipUnlessSupported();

        var id = await AShipmentAsync();

        var stored = await LoadAsync(id);
        stored.Supplier.ShouldBe("Acme");
        stored.Status.ShouldBe("draft");
        stored.Version.ShouldNotBe(Guid.Empty,
            "The store committed a versioned document without stamping a version, so there is nothing for a later write to be guarded against.");
    }

    /// <summary>
    /// <b>The fact fisher#245 failed.</b> A document loaded in one session and stored through another
    /// succeeds. This is the request-per-session workflow the feature exists for, and it is the half
    /// a session-scoped version tracker cannot answer: the writing session never read the row, so it
    /// has no recorded expectation to guard with — and refusing on that basis refuses every
    /// legitimate write.
    /// </summary>
    [Fact]
    public async Task a_document_loaded_in_one_session_can_be_stored_through_another()
    {
        SkipUnlessSupported();

        var id = await AShipmentAsync();

        // Request 1 loads.
        var loaded = await LoadAsync(id);

        // Request 2 writes, through a session that has never seen this row.
        loaded.Status = "shipped";
        await StoreAsync(loaded);

        var stored = await LoadAsync(id);
        stored.Status.ShouldBe("shipped");
    }

    /// <summary>
    /// <b>The twin.</b> A genuinely stale instance is still refused, and the row keeps the winner's
    /// value.
    /// </summary>
    /// <remarks>
    /// Without this, the cheap fix for the fact above — stop guarding, or seed the guard from
    /// whatever the row currently holds — passes. Both halves together are what say the guard is
    /// fed from the <em>document's</em> version rather than from the session's memory or from the
    /// database's present state.
    /// </remarks>
    [Fact]
    public async Task a_stale_instance_is_refused_and_the_winner_stands()
    {
        SkipUnlessSupported();

        var id = await AShipmentAsync();

        // Two independent reads of the same row, each in its own session.
        var first = await LoadAsync(id);
        var second = await LoadAsync(id);

        first.Status = "shipped";
        await StoreAsync(first);

        second.Status = "cancelled";
        await StoreShouldBeRefusedAsync(second);

        // Refused rather than partially applied.
        var stored = await LoadAsync(id);
        stored.Status.ShouldBe("shipped");
    }

    /// <summary>
    /// A successful write moves the stored instance's own <see cref="IVersioned.Version" /> on, so
    /// the same instance can be stored again without tripping its own guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinned as contract rather than left as coincidence because it is what makes a long-lived
    /// instance usable at all: without it, every caller has to reload between writes, and the
    /// natural spelling — mutate, store, mutate, store — fails on its second call for no reason the
    /// caller can see.
    /// </para>
    /// <para>
    /// The only fact in this suite that reads the version off the instance that was stored rather
    /// than off a freshly loaded one, and deliberately so: write-back onto the caller's instance is
    /// precisely what is being asserted. <see cref="NumericRevisionCompliance{TFixture}" /> reads
    /// the other way round for the mirror-image reason — there the statement is about what is
    /// <em>stored</em>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task a_successful_write_moves_the_instances_own_version_on()
    {
        SkipUnlessSupported();

        var id = await AShipmentAsync();

        var shipment = await LoadAsync(id);
        var loadedVersion = shipment.Version;

        shipment.Status = "shipped";
        await StoreAsync(shipment);

        shipment.Version.ShouldNotBe(loadedVersion,
            "The store committed a write without moving the instance's Version on, so storing the same instance again guards on a version that is no longer current.");

        // The consequence, which is the reason the write-back matters.
        shipment.Status = "delivered";
        await StoreAsync(shipment);

        (await LoadAsync(id)).Status.ShouldBe("delivered");
    }

    /// <summary>
    /// The control: a document type with no version member is unaffected. Two writes of the same
    /// instance across sessions, no guard, no exception.
    /// </summary>
    /// <remarks>
    /// Worth carrying because the cheap over-reaction to the facts above is a store-wide guard, and a
    /// store-wide guard is a breaking change for every document that never asked for one.
    /// </remarks>
    [Fact]
    public async Task a_type_with_no_version_member_is_unguarded()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Gear", Color = "red", Weight = 3 });

        await using (var query = QuerySession())
        {
            var loaded = await query.LoadAsync<ComplianceWidget>(id, Cancellation);
            loaded.ShouldNotBeNull();
            loaded.Color = "blue";
            await PersistAsync(loaded);
        }

        // The same stale instance again -- still no guard, because this type never declared one.
        await using (var query = QuerySession())
        {
            var stale = await query.LoadAsync<ComplianceWidget>(id, Cancellation);
            stale.ShouldNotBeNull();
            stale.Weight = 9;
            await PersistAsync(stale);
        }

        await using var final = QuerySession();
        var widget = await final.LoadAsync<ComplianceWidget>(id, Cancellation);
        widget.ShouldNotBeNull();
        widget.Weight.ShouldBe(9);
    }
}
