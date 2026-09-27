using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Conjoined <em>document</em> tenancy — one database, many tenants, every document row scoped to a
/// tenant id (jasperfx#898).
/// </summary>
/// <remarks>
/// <para>
/// The document mirror of
/// <see cref="ConjoinedEventTenancyCompliance{TFixture,TOperations,TQuerySession}" />, and it did not
/// exist until jasperfx#898: the event side had twelve facts enrolled by all three stores while
/// <see cref="DocumentComplianceConfig" /> and every <c>Document*Compliance</c> suite had no tenancy
/// seam at all. <c>DocumentSearchCompliance</c> cites fisher#285 — a store ignoring conjoined tenancy
/// in its search path — as a reason to exist, and then configures no tenancy, which is the shape of
/// the gap.
/// </para>
/// <para>
/// Two things carry the weight, and both are borrowed from the event suite because they are what
/// makes an isolation assertion sharp rather than vacuous.
/// </para>
/// <para>
/// <b>Every fact checks both directions.</b> A store that leaks across tenants still returns correct
/// answers for the tenant that happens to own the data and only misbehaves for the other one, so
/// asserting "tenant A sees its row" catches nothing on its own.
/// </para>
/// <para>
/// <b>Every fact reuses one document id across two tenants.</b> Under conjoined tenancy the identity
/// of a document is (tenant, id), not id alone. A store keying on id alone does not fail loudly — it
/// folds the two tenants' writes into one row, so the second write silently overwrites the first and
/// both tenants then read the same document. Distinct ids per tenant, which is what every tenanted
/// fact in this library used before, passes cleanly on exactly that store.
/// </para>
/// <para>
/// <b>What this suite deliberately does not cover</b>, because tenancy is not a reason to reopen a
/// settled boundary. jasperfx#898 also proposed tenant-scoping facts for bulk insert, patching,
/// soft deletes and a delete-all-tenant-data administration call. Those four are on the README's
/// settled out-of-scope list for the document contract — they are the surfaces jasperfx#647
/// deliberately declined to abstract — and a tenant axis does not bring them back in. The line is
/// the same one <c>DocumentQueryCompliance</c> holds: the operators exercised below are that suite's
/// closed minimum translatable set plus the async terminators, not a wider set chosen because
/// tenancy made them interesting.
/// </para>
/// <para>
/// Cost is one config member (<see cref="DocumentComplianceConfig.ConjoinedDocuments" />), the two
/// tenant-scoped session overloads on the contract itself, and — only for the two escape facts — one
/// pair of fixture seam members. The sessions are the contract rather than a fixture seam on
/// purpose: the document fixture's own rule is that a suite needing to reach past the interfaces
/// means the contract has the hole.
/// </para>
/// </remarks>
public abstract class DocumentConjoinedTenancyCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private const string TenantA = "acme";
    private const string TenantB = "globex";
    private const string TenantC = "initech";

    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_conjoined_docs";

        config.AddDocumentType<ComplianceWidget>();
        config.AddDocumentType<ComplianceGadget>();
        config.AddDocumentType<ComplianceShipment>();
        config.AddDocumentType<ComplianceLedgerEntry>();

        config.Conjoined<ComplianceWidget>();
        config.Conjoined<ComplianceGadget>();
        config.Conjoined<ComplianceShipment>();
        config.Conjoined<ComplianceLedgerEntry>();

        config.UseOptimisticConcurrency<ComplianceShipment>();
        config.UseNumericRevisions<ComplianceLedgerEntry>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    /// <summary>
    /// Short-circuits configuration as well as the facts, matching
    /// <see cref="UpcastingCompliance{TFixture,TOperations,TQuerySession}" />. A store with no
    /// conjoined document tenancy may fail while <em>building</em> a store whose document types are
    /// declared multi-tenanted, which would fail every fact for a reason that has nothing to do with
    /// the behaviour under test.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync().ConfigureAwait(false);

        if (!theFixture.SupportsConjoinedDocuments)
        {
            return;
        }

        await theFixture.ConfigureAsync(Configuration).ConfigureAwait(false);
        await theFixture.CleanDocumentDataAsync().ConfigureAwait(false);
    }

    private void SkipUnlessConjoinedDocumentsAreSupported()
    {
        Assert.SkipUnless(theFixture.SupportsConjoinedDocuments,
            "This store does not slice documents by tenant within one database (jasperfx#898).");
    }

    private void SkipUnlessCrossTenantQueriesAreSupported()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        Assert.SkipUnless(theFixture.SupportsCrossTenantQueries,
            "This store does not implement the AnyTenant / TenantIsOneOf escapes from tenant scoping.");
    }

    /// <summary>
    /// Store documents in one tenant's scope and commit them.
    /// </summary>
    private async Task PersistForAsync<T>(string tenantId, params T[] documents) where T : notnull
    {
        await using var session = LightweightSession(tenantId);
        session.Store(documents);
        await session.SaveChangesAsync(Cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// The arrangement every fact below starts from: one widget id, written in both tenants, with the
    /// owning tenant's name in <c>Color</c> so a leak is visible in the value rather than only in the
    /// count.
    /// </summary>
    private async Task<Guid> ArrangeSharedWidgetAsync()
    {
        var shared = Guid.NewGuid();

        await PersistForAsync(TenantA,
            new ComplianceWidget { Id = shared, Name = "Shared", Color = TenantA, Weight = 1 },
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "OnlyA", Color = TenantA, Weight = 2 });

        await PersistForAsync(TenantB,
            new ComplianceWidget { Id = shared, Name = "Shared", Color = TenantB, Weight = 10 });

        return shared;
    }

    /// <summary>
    /// The fisher#51 shape: a query with no <c>Where</c> leaking every tenant's rows. Every route a
    /// store-agnostic caller has to reach documents is exercised, because a store can scope one and
    /// miss another — the predicate is usually where the tenant filter is injected, so the shapes most
    /// likely to leak are exactly the ones that carry no predicate of their own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operator set is <see cref="DocumentQueryCompliance{TFixture}" />'s closed minimum
    /// translatable set plus the four async terminators, and that is a deliberate limit rather than
    /// what happened to be convenient. <c>GroupBy</c>, <c>Sum</c>, <c>LoadMany</c> and compiled queries
    /// were all proposed for this fact in jasperfx#898; none of them is on the document contract or in
    /// that closed set, and pinning tenant scoping over them would widen a boundary the README calls
    /// closed by measurement. Tenancy is not a reason to grow the LINQ surface this library holds
    /// stores to.
    /// </para>
    /// <para>
    /// Both directions on every shape, and the counts are asymmetric (two rows in A, one in B) so a
    /// store that leaked would have to leak <em>consistently in both directions</em> to pass — which a
    /// missing tenant predicate never does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task every_read_shape_with_no_where_is_scoped_to_the_session_tenant()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        await ArrangeSharedWidgetAsync();

        await using (var a = QuerySession(TenantA))
        {
            (await a.Query<ComplianceWidget>().ToListAsync(Cancellation))
                .ShouldAllBe(x => x.Color == TenantA);
            (await a.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(2);
            (await a.Query<ComplianceWidget>().AnyAsync(Cancellation)).ShouldBeTrue();

            // Operator chains, one per member of the closed set. Each carries no tenant predicate of
            // its own, so each is a separate chance for a store to compose the scope in only some of
            // its query paths.
            (await a.Query<ComplianceWidget>().Where(x => x.Weight > 0).ToListAsync(Cancellation))
                .Count.ShouldBe(2);
            (await a.Query<ComplianceWidget>().Select(x => x.Color).Distinct().ToListAsync(Cancellation))
                .ShouldBe([TenantA]);
            (await a.Query<ComplianceWidget>().OrderBy(x => x.Weight).Take(10).ToListAsync(Cancellation))
                .ShouldAllBe(x => x.Color == TenantA);
            (await a.Query<ComplianceWidget>().OrderByDescending(x => x.Weight).Skip(1)
                .ToListAsync(Cancellation)).ShouldAllBe(x => x.Color == TenantA);

            (await a.Query<ComplianceWidget>().OrderBy(x => x.Weight)
                .FirstOrDefaultAsync(Cancellation)).ShouldNotBeNull().Color.ShouldBe(TenantA);
        }

        // The other direction. Tenant B holds exactly one row, and its weight is the largest in the
        // database -- so a leak into B shows up as a count, and a leak out of B as a missing row.
        await using var b = QuerySession(TenantB);

        (await b.Query<ComplianceWidget>().ToListAsync(Cancellation))
            .ShouldHaveSingleItem().Color.ShouldBe(TenantB);
        (await b.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
        (await b.Query<ComplianceWidget>().AnyAsync(Cancellation)).ShouldBeTrue();
        (await b.Query<ComplianceWidget>().Select(x => x.Color).Distinct().ToListAsync(Cancellation))
            .ShouldBe([TenantB]);
        (await b.Query<ComplianceWidget>().OrderBy(x => x.Weight)
            .FirstOrDefaultAsync(Cancellation)).ShouldNotBeNull().Weight.ShouldBe(10);
    }

    /// <summary>
    /// A tenant that holds nothing reads nothing — the emptiness half of the same contract.
    /// </summary>
    /// <remarks>
    /// Separate from the fact above because it is the one arrangement where a leak is unambiguous:
    /// every shape must answer empty, so there is no "correct answer for the tenant that owns the
    /// data" to hide behind. <c>AnyAsync</c> is the sharp one — a store whose scope reaches
    /// <c>ToListAsync</c> but not its existence check answers true over another tenant's rows.
    /// </remarks>
    [Fact]
    public async Task a_tenant_holding_nothing_reads_nothing_through_every_shape()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        await ArrangeSharedWidgetAsync();

        await using var c = QuerySession(TenantC);

        (await c.Query<ComplianceWidget>().ToListAsync(Cancellation)).ShouldBeEmpty();
        (await c.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(0);
        (await c.Query<ComplianceWidget>().AnyAsync(Cancellation)).ShouldBeFalse();
        (await c.Query<ComplianceWidget>().Where(x => x.Weight > 0).ToListAsync(Cancellation))
            .ShouldBeEmpty();
        (await c.Query<ComplianceWidget>().OrderBy(x => x.Weight)
            .FirstOrDefaultAsync(Cancellation)).ShouldBeNull();
    }

    /// <summary>
    /// <c>LoadAsync</c> of an id both tenants hold returns the session tenant's row, on all three
    /// identity overloads (marten#4801's shape).
    /// </summary>
    /// <remarks>
    /// The load path is worth its own fact rather than folding into the query one: on every store it
    /// is a separate code path from the LINQ provider — a keyed fetch, often with an identity map in
    /// front of it — so a tenant predicate correctly compiled into the query translator reaches none
    /// of it. Two sessions on the same id in one test is also the identity-map shape: a map keyed on
    /// id alone hands the second tenant the first tenant's instance.
    /// </remarks>
    [Fact]
    public async Task load_of_a_shared_id_returns_the_session_tenants_document()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        var shared = await ArrangeSharedWidgetAsync();

        var sharedKey = "shared-gadget";
        await PersistForAsync(TenantA, new ComplianceGadget { Id = sharedKey, Kind = TenantA, Weight = 1 });
        await PersistForAsync(TenantB, new ComplianceGadget { Id = sharedKey, Kind = TenantB, Weight = 10 });

        await using (var a = QuerySession(TenantA))
        {
            (await a.LoadAsync<ComplianceWidget>(shared, Cancellation))
                .ShouldNotBeNull().Color.ShouldBe(TenantA);
            (await a.LoadAsync<ComplianceGadget>(sharedKey, Cancellation))
                .ShouldNotBeNull().Kind.ShouldBe(TenantA);

            // The jasperfx#665 boxed overload, which routes through the store's value-type
            // resolution rather than either typed path.
            (await a.LoadAsync<ComplianceWidget>((object)shared, Cancellation))
                .ShouldNotBeNull().Color.ShouldBe(TenantA);
        }

        await using var b = QuerySession(TenantB);

        (await b.LoadAsync<ComplianceWidget>(shared, Cancellation))
            .ShouldNotBeNull().Color.ShouldBe(TenantB);
        (await b.LoadAsync<ComplianceGadget>(sharedKey, Cancellation))
            .ShouldNotBeNull().Kind.ShouldBe(TenantB);
        (await b.LoadAsync<ComplianceWidget>((object)shared, Cancellation))
            .ShouldNotBeNull().Color.ShouldBe(TenantB);
    }

    /// <summary>
    /// A tenant that never stored the id loads null, rather than the other tenant's document.
    /// </summary>
    [Fact]
    public async Task load_of_another_tenants_id_returns_null()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        var shared = await ArrangeSharedWidgetAsync();

        await using var c = QuerySession(TenantC);

        (await c.LoadAsync<ComplianceWidget>(shared, Cancellation)).ShouldBeNull();
    }

    /// <summary>
    /// Writing the same id in two tenants produces two documents, not one overwritten one.
    /// </summary>
    /// <remarks>
    /// The write-side statement of the identity rule, and the fact that fails loudly on the store the
    /// whole suite is aimed at. A store keying documents on id alone passes every read fact above
    /// <em>vacuously</em> once this one has failed — both tenants read the same surviving row, and
    /// both read it "correctly" — so the second write landing as its own row has to be asserted on its
    /// own. Asserted by reading each tenant's copy back rather than by counting across tenants,
    /// because counting across tenants needs the escape seam, which is separately gated.
    /// </remarks>
    [Fact]
    public async Task the_same_document_id_lives_independently_in_two_tenants()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        var shared = Guid.NewGuid();

        await PersistForAsync(TenantA,
            new ComplianceWidget { Id = shared, Name = "A", Color = TenantA, Weight = 1 });
        await PersistForAsync(TenantB,
            new ComplianceWidget { Id = shared, Name = "B", Color = TenantB, Weight = 10 });

        // Tenant A's row must still hold its own values after tenant B wrote the same id -- an
        // overwriting store answers "B" here for both.
        await using (var a = QuerySession(TenantA))
        {
            var mine = (await a.LoadAsync<ComplianceWidget>(shared, Cancellation)).ShouldNotBeNull();
            mine.Name.ShouldBe("A");
            mine.Weight.ShouldBe(1);
            (await a.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
        }

        await using var b = QuerySession(TenantB);

        var theirs = (await b.LoadAsync<ComplianceWidget>(shared, Cancellation)).ShouldNotBeNull();
        theirs.Name.ShouldBe("B");
        theirs.Weight.ShouldBe(10);
        (await b.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
    }

    /// <summary>
    /// A later store in one tenant updates that tenant's row and leaves the other's alone.
    /// </summary>
    /// <remarks>
    /// The update half is a distinct claim from the insert half above, and distinct in the way that
    /// matters: a store deciding insert-vs-update from "does a row with this id exist" rather than
    /// "does a row with this (tenant, id) exist" gets the first write right and the second wrong. On
    /// that store tenant B's first write is compiled as an <em>update</em> of tenant A's row, which
    /// is the overwrite the previous fact catches — and this one catches its mirror image, where the
    /// update is correctly scoped but the store re-stamps the row's tenant on the way through.
    /// </remarks>
    [Fact]
    public async Task updating_in_one_tenant_leaves_the_other_tenants_document_alone()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        var shared = await ArrangeSharedWidgetAsync();

        await PersistForAsync(TenantA,
            new ComplianceWidget { Id = shared, Name = "Revised", Color = TenantA, Weight = 99 });

        await using (var a = QuerySession(TenantA))
        {
            var mine = (await a.LoadAsync<ComplianceWidget>(shared, Cancellation)).ShouldNotBeNull();
            mine.Name.ShouldBe("Revised");
            mine.Weight.ShouldBe(99);

            // Still two rows: the write was an update of A's own row, not an insert of a third one.
            (await a.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(2);
        }

        await using var b = QuerySession(TenantB);

        var theirs = (await b.LoadAsync<ComplianceWidget>(shared, Cancellation)).ShouldNotBeNull();
        theirs.Name.ShouldBe("Shared");
        theirs.Weight.ShouldBe(10);
        (await b.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
    }

    /// <summary>
    /// Every deletion overload is scoped to the session's tenant.
    /// </summary>
    /// <remarks>
    /// All three routes, because they compile differently on every store: <c>Delete(entity)</c> takes
    /// the identity off the document, <c>Delete&lt;T&gt;(id)</c> is handed one, and
    /// <c>DeleteWhere</c> is a predicate the provider translates. The last is the dangerous one — it
    /// is the only deletion that reaches rows the caller never named, so a missing tenant predicate
    /// there deletes every tenant's matching documents.
    /// </remarks>
    [Fact]
    public async Task deletes_are_scoped_to_the_session_tenant_for_a_shared_id()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        var shared = await ArrangeSharedWidgetAsync();

        await using (var a = LightweightSession(TenantA))
        {
            a.Delete<ComplianceWidget>(shared);
            await a.SaveChangesAsync(Cancellation);
        }

        await using (var b = QuerySession(TenantB))
        {
            (await b.LoadAsync<ComplianceWidget>(shared, Cancellation))
                .ShouldNotBeNull().Color.ShouldBe(TenantB);
        }

        // Delete(entity): the identity comes off the document, and the document was loaded in B's
        // scope, so a store resolving the deletion by id alone reaches A's remaining row.
        var sharedKey = "shared-gadget";
        await PersistForAsync(TenantA, new ComplianceGadget { Id = sharedKey, Kind = TenantA, Weight = 1 });
        await PersistForAsync(TenantB, new ComplianceGadget { Id = sharedKey, Kind = TenantB, Weight = 10 });

        await using (var b = LightweightSession(TenantB))
        {
            var theirs = (await b.LoadAsync<ComplianceGadget>(sharedKey, Cancellation)).ShouldNotBeNull();
            b.Delete(theirs);
            await b.SaveChangesAsync(Cancellation);
        }

        await using (var a = QuerySession(TenantA))
        {
            (await a.LoadAsync<ComplianceGadget>(sharedKey, Cancellation))
                .ShouldNotBeNull().Kind.ShouldBe(TenantA);
        }

        // DeleteWhere: the predicate matches in both tenants, and only the session's tenant may lose
        // rows.
        await using (var b = LightweightSession(TenantB))
        {
            b.DeleteWhere<ComplianceWidget>(x => x.Weight > 0);
            await b.SaveChangesAsync(Cancellation);
        }

        await using (var b = QuerySession(TenantB))
        {
            (await b.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(0);
        }

        await using var stillThere = QuerySession(TenantA);
        (await stillThere.Query<ComplianceWidget>().CountAsync(Cancellation)).ShouldBe(1);
    }

    /// <summary>
    /// <see cref="JasperFx.Metadata.IVersioned" /> optimistic concurrency is guarded per (tenant, id),
    /// not per id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on <see cref="DocumentStorageComplianceFixture.SupportsOptimisticConcurrency" /> rather
    /// than folded into <see cref="GuidOptimisticConcurrencyCompliance{TFixture}" />, because what is
    /// under test is the <em>scope</em> of the guard and that suite's store is single-tenanted.
    /// </para>
    /// <para>
    /// The failure this catches is specific and silent: a guard whose WHERE clause matches on
    /// <c>(id, version)</c> and omits the tenant updates zero rows when another tenant happens to
    /// hold the same id at a different version, and the store reports a
    /// <see cref="JasperFx.ConcurrencyException" /> over a write that was never in conflict with
    /// anything. Both directions again — B's write must succeed while A's stored version is stale,
    /// and A's genuinely stale write must still be refused.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task optimistic_concurrency_is_scoped_to_the_tenant_for_a_shared_id()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        Assert.SkipUnless(theFixture.SupportsOptimisticConcurrency,
            "This store does not implement Guid optimistic concurrency (jasperfx#819).");

        var shared = Guid.NewGuid();

        await PersistForAsync(TenantA, new ComplianceShipment { Id = shared, Supplier = TenantA, Status = "new" });
        await PersistForAsync(TenantB, new ComplianceShipment { Id = shared, Supplier = TenantB, Status = "new" });

        // Move A's version on, so that A's stored version and B's stored version now disagree. A
        // guard that ignores the tenant is reading whichever row it finds first from here on.
        ComplianceShipment reloadedForA;
        await using (var a = LightweightSession(TenantA))
        {
            reloadedForA = (await a.LoadAsync<ComplianceShipment>(shared, Cancellation)).ShouldNotBeNull();
            reloadedForA.Status = "advanced";
            a.Store(reloadedForA);
            await a.SaveChangesAsync(Cancellation);
        }

        // B's write is not in conflict with anything. It must succeed.
        await using (var b = LightweightSession(TenantB))
        {
            var theirs = (await b.LoadAsync<ComplianceShipment>(shared, Cancellation)).ShouldNotBeNull();
            theirs.Status = "shipped";
            b.Store(theirs);
            await b.SaveChangesAsync(Cancellation);
        }

        await using (var b = QuerySession(TenantB))
        {
            (await b.LoadAsync<ComplianceShipment>(shared, Cancellation))
                .ShouldNotBeNull().Status.ShouldBe("shipped");
        }

        // The other direction: the guard still has teeth inside a tenant. `reloadedForA` carries the
        // version A held BEFORE its own update above, so this really is stale.
        await using var stale = LightweightSession(TenantA);
        reloadedForA.Status = "stale";
        stale.Store(reloadedForA);

        await Should.ThrowAsync<JasperFx.ConcurrencyException>(
            () => stale.SaveChangesAsync(Cancellation));
    }

    /// <summary>
    /// <see cref="IRevisioned" /> numeric revisions are counted per (tenant, id).
    /// </summary>
    /// <remarks>
    /// The same claim as the fact above over the other concurrency mechanism, and worth stating
    /// separately because the two are separate storage columns with separate guards on every store.
    /// The sharp assertion is the landed revision rather than the refusal: a store counting revisions
    /// per id hands the second tenant's first insert revision 2, which is a wrong answer that throws
    /// nothing and is invisible to a caller who never compares across tenants.
    /// </remarks>
    [Fact]
    public async Task numeric_revisions_are_counted_per_tenant_for_a_shared_id()
    {
        SkipUnlessConjoinedDocumentsAreSupported();

        Assert.SkipUnless(theFixture.SupportsNumericRevisions,
            "This store does not implement numeric revisions (jasperfx#785 §4.2).");

        var shared = Guid.NewGuid();

        await PersistForAsync(TenantA, new ComplianceLedgerEntry { Id = shared, Customer = TenantA, Amount = 1 });

        // Move A to revision 3, so a store counting per id would hand B's first insert a 4.
        for (var i = 0; i < 2; i++)
        {
            await using var a = LightweightSession(TenantA);
            var mine = (await a.LoadAsync<ComplianceLedgerEntry>(shared, Cancellation)).ShouldNotBeNull();
            mine.Version++;
            a.Store(mine);
            await a.SaveChangesAsync(Cancellation);
        }

        await PersistForAsync(TenantB, new ComplianceLedgerEntry { Id = shared, Customer = TenantB, Amount = 100 });

        await using (var b = QuerySession(TenantB))
        {
            var theirs = (await b.LoadAsync<ComplianceLedgerEntry>(shared, Cancellation)).ShouldNotBeNull();
            theirs.Amount.ShouldBe(100);
            theirs.Version.ShouldBe(1);
        }

        await using var a2 = QuerySession(TenantA);
        var stillMine = (await a2.LoadAsync<ComplianceLedgerEntry>(shared, Cancellation)).ShouldNotBeNull();
        stillMine.Amount.ShouldBe(1);
        stillMine.Version.ShouldBe(3);
    }

    /// <summary>
    /// The deliberate escapes: <c>AnyTenant</c> reads across the boundary, and <c>TenantIsOneOf</c>
    /// narrows it to a named set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on <see cref="DocumentStorageComplianceFixture.SupportsCrossTenantQueries" />, because a
    /// store can enforce the whole isolation contract above and offer no way out of it.
    /// </para>
    /// <para>
    /// The two are one fact rather than two, and the ordering inside it is what gives it teeth: a
    /// store whose escape is really "drop the tenant filter entirely" satisfies the <c>AnyTenant</c>
    /// half perfectly and then hands <c>TenantIsOneOf</c> every tenant's rows as well. Asserting the
    /// narrow case against the wide one in the same arrangement is what distinguishes them, so the
    /// third tenant exists only to be excluded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task any_tenant_reads_across_the_boundary_and_tenant_is_one_of_narrows_it()
    {
        SkipUnlessCrossTenantQueriesAreSupported();

        var shared = await ArrangeSharedWidgetAsync();

        await PersistForAsync(TenantC,
            new ComplianceWidget { Id = Guid.NewGuid(), Name = "OnlyC", Color = TenantC, Weight = 100 });

        await using var session = QuerySession(TenantA);

        var everything = await theFixture.QueryAllTenantsAsync<ComplianceWidget>(session, Cancellation);

        everything.Count.ShouldBe(4);
        everything.Select(x => x.Color).Distinct().OrderBy(x => x)
            .ShouldBe([TenantA, TenantB, TenantC]);

        // The shared id appears once per tenant that wrote it, which is the identity rule stated from
        // the only vantage point that can see both rows at once.
        everything.Count(x => x.Id == shared).ShouldBe(2);

        var narrowed = await theFixture.QueryTenantsAsync<ComplianceWidget>(
            session, [TenantB, TenantC], Cancellation);

        narrowed.Count.ShouldBe(2);
        narrowed.Select(x => x.Color).OrderBy(x => x).ShouldBe([TenantB, TenantC]);
    }
}
