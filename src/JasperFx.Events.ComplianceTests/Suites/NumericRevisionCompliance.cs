using System;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The nine numeric-revision facts, over whichever document type and declaration route the derived
/// suite supplies.
/// </summary>
/// <typeparam name="TFixture">The store's concrete document fixture.</typeparam>
/// <typeparam name="TDoc">The document under test.</typeparam>
/// <remarks>
/// <para>
/// Split out of <see cref="NumericRevisionCompliance{TFixture}" /> by jasperfx#819 §2. The nine facts
/// are thorough about revision <em>semantics</em>, and the one thing they could not vary was how the
/// document says it uses numeric revisions — because the suite was written against
/// <see cref="IRevisioned" /> throughout. That was not an oversight; it was the only route the shared
/// config could express. fisher#228 is what lived in the gap: <c>Store(doc, revision)</c> and the
/// operation's expected revision were both gated on the marker, so a type that opted in through
/// <c>Schema.For&lt;T&gt;().UseNumericRevisions()</c> had its supplied revision dropped and guarded on
/// <c>0</c> — which means auto. A backwards write was accepted and nothing was dropped: a silent lost
/// update, in the method whose entire purpose is to prevent one. The two routes are documented as
/// equivalent and only one of them worked.
/// </para>
/// <para>
/// Three hooks, all of them about <em>reaching</em> the document rather than about behavior:
/// construction and two member reads. The marker suite reads them through
/// <see cref="IRevisioned" />; the declared suite reads the same members off a type that implements
/// nothing.
/// </para>
/// </remarks>
public abstract class NumericRevisionComplianceBase<TFixture, TDoc> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
    where TDoc : class
{
    /// <summary>
    /// Build one document. A fresh instance every time so a store that writes the landed revision
    /// back onto the caller's document cannot leak state from one step of a test into the next.
    /// </summary>
    protected abstract TDoc NewDocument(Guid id, string customer, int version);

    /// <summary>
    /// The document's revision member, however it was declared.
    /// </summary>
    protected abstract int RevisionOf(TDoc document);

    /// <summary>
    /// The document's one ordinary data member, so a refused write can be shown to have been refused
    /// rather than partially applied.
    /// </summary>
    protected abstract string CustomerOf(TDoc document);

    /// <summary>
    /// The capability gate. Overridden by a derived suite that needs a narrower one.
    /// </summary>
    protected virtual void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsNumericRevisions,
            "This document store does not implement numeric revisions");
    }

    /// <summary>
    /// Store one document at the named revision, committing in its own session.
    /// </summary>
    private async Task StoreAsync(Guid id, string customer, int version)
    {
        await using var session = LightweightSession();
        session.Store(NewDocument(id, customer, version));
        await session.SaveChangesAsync(Cancellation);
    }

    private async Task<ConcurrencyException> StoreShouldBeRefusedAsync(Guid id, string customer, int version)
        => await Should.ThrowAsync<ConcurrencyException>(() => StoreAsync(id, customer, version));

    private async Task<TDoc> LoadAsync(Guid id)
    {
        await using var query = QuerySession();
        var loaded = await query.LoadAsync<TDoc>(id, Cancellation);
        loaded.ShouldNotBeNull();
        return loaded;
    }

    /// <summary>
    /// The stored revision, read off a freshly loaded document — the only route the shared contract
    /// offers, and the one that is a statement about storage.
    /// </summary>
    private async Task<int> StoredRevisionAsync(Guid id) => RevisionOf(await LoadAsync(id));

    private async Task<string> StoredCustomerAsync(Guid id) => CustomerOf(await LoadAsync(id));

    /// <summary>
    /// The baseline every other fact is measured against: a document arriving with no revision on it
    /// is an auto insert, and auto starts at one rather than zero.
    /// </summary>
    [Fact]
    public async Task a_new_document_lands_at_revision_one()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme", 0);

        (await StoredRevisionAsync(id)).ShouldBe(1);
    }

    /// <summary>
    /// <b>The insert-path ruling.</b> A brand-new document carrying an explicit revision lands at
    /// exactly that revision. The revision is honoured, not discarded in favour of 1 — the same rule
    /// the update path follows, one row earlier: an explicit revision is the version the caller is
    /// asking for, and on an insert there is nothing stored for it to have to exceed.
    /// </summary>
    /// <remarks>
    /// This is a jump on first write, and it is the capability that makes importing a record at the
    /// revision it already had, or seeding a document at a version decided upstream, expressible at
    /// all. A store that always starts at 1 forces the caller to insert and then immediately update.
    /// </remarks>
    [Fact]
    public async Task a_new_document_lands_at_an_explicit_revision()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme, imported at seven", 7);

        (await StoredRevisionAsync(id)).ShouldBe(7);
        (await StoredCustomerAsync(id)).ShouldBe("Acme, imported at seven");
    }

    /// <summary>
    /// The insert really wrote 7 — the store is counting from it, not merely round-tripping the value
    /// it was handed.
    /// </summary>
    /// <remarks>
    /// This fact exists because the one above can pass <em>vacuously</em>. A store that ignores
    /// revisions entirely still serializes the revision member as an ordinary property and hands the
    /// same 7 back on load, so reading the revision off a loaded document cannot by itself
    /// distinguish "stored a revision of 7" from "stored a document with a field set to 7".
    /// Following the insert with an auto store settles it: only a real stored revision increments to
    /// 8, and only a real guard refuses the re-store at 7.
    /// </remarks>
    [Fact]
    public async Task an_explicit_insert_revision_becomes_the_stored_revision()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme, imported at seven", 7);

        // The strictly-greater guard now applies from 7, not from 1.
        await StoreShouldBeRefusedAsync(id, "Acme, stale", 7);

        // And auto counts on from 7 rather than restarting.
        await StoreAsync(id, "Acme, moved on", 0);
        (await StoredRevisionAsync(id)).ShouldBe(8);
        (await StoredCustomerAsync(id)).ShouldBe("Acme, moved on");
    }

    /// <summary>
    /// Revision <c>0</c> means auto — increment whatever is stored — and it is the escape hatch from
    /// the sharp edge below.
    /// </summary>
    [Fact]
    public async Task an_auto_revision_increments_the_stored_value()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme", 0);
        await StoreAsync(id, "Acme, renamed", 0);
        await StoreAsync(id, "Acme, renamed twice", 0);

        (await StoredRevisionAsync(id)).ShouldBe(3);
        (await StoredCustomerAsync(id)).ShouldBe("Acme, renamed twice");
    }

    /// <summary>
    /// The supported way to move a loaded document forward: name the revision it is going to. Note
    /// that it lands at exactly the revision named — the explicit branch assigns the caller's value,
    /// it does not increment past it.
    /// </summary>
    [Fact]
    public async Task an_explicit_revision_greater_than_the_stored_one_is_accepted()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme", 0);

        await StoreAsync(id, "Acme, revised", 2);

        (await StoredRevisionAsync(id)).ShouldBe(2);
        (await StoredCustomerAsync(id)).ShouldBe("Acme, revised");
    }

    /// <summary>
    /// <b>The load-bearing fact, and the one the ruling turns on.</b> Re-storing an instance still
    /// carrying the revision it was loaded at is a concurrency failure, not an increment: the
    /// supplied revision must be strictly greater, and equal is not greater. The natural
    /// read-modify-write spelling is therefore <c>doc.Version + 1</c>, never <c>doc.Version</c>.
    /// </summary>
    /// <remarks>
    /// Asserted as the shared <see cref="ConcurrencyException" /> from <c>JasperFx</c> rather than any
    /// store's own type. Every Critter Stack store already throws this one — a store-specific subclass
    /// still satisfies it, a store-specific sibling does not, and the difference is precisely what a
    /// store-agnostic consumer catching the base type would hit.
    /// </remarks>
    [Fact]
    public async Task an_explicit_revision_equal_to_the_stored_one_is_refused()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme", 0);
        (await StoredRevisionAsync(id)).ShouldBe(1);

        await StoreShouldBeRefusedAsync(id, "Acme, stale", 1);

        // Refused rather than partially applied.
        (await StoredRevisionAsync(id)).ShouldBe(1);
        (await StoredCustomerAsync(id)).ShouldBe("Acme");
    }

    /// <summary>
    /// The unambiguous stale write — a revision below what is stored — is refused for the same reason
    /// and by the same guard.
    /// </summary>
    [Fact]
    public async Task an_explicit_revision_below_the_stored_one_is_refused()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme", 0);
        await StoreAsync(id, "Acme, second", 0);
        await StoreAsync(id, "Acme, third", 0);
        (await StoredRevisionAsync(id)).ShouldBe(3);

        await StoreShouldBeRefusedAsync(id, "Acme, way behind", 2);

        (await StoredRevisionAsync(id)).ShouldBe(3);
        (await StoredCustomerAsync(id)).ShouldBe("Acme, third");
    }

    /// <summary>
    /// The capability an equality rule cannot express: an explicit revision may skip ahead rather than
    /// step by one, and the document lands at exactly the revision named. This is a large part of why
    /// the ruling went to strictly-greater — it is what lets a caller adopt a revision decided
    /// somewhere else instead of one the store happens to be counting.
    /// </summary>
    [Fact]
    public async Task an_explicit_revision_may_jump_non_contiguously()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme", 0);
        await StoreAsync(id, "Acme, second", 0);
        await StoreAsync(id, "Acme, third", 0);
        (await StoredRevisionAsync(id)).ShouldBe(3);

        await StoreAsync(id, "Acme, imported", 10);

        (await StoredRevisionAsync(id)).ShouldBe(10);
        (await StoredCustomerAsync(id)).ShouldBe("Acme, imported");
    }

    /// <summary>
    /// The other half of "auto always wins": the <c>0</c> sentinel is not compared against anything,
    /// so it succeeds even where the stored revision is far ahead of any revision the caller has seen
    /// — and it resumes counting from what is stored, not from what the caller last knew.
    /// </summary>
    [Fact]
    public async Task an_auto_revision_wins_even_when_the_stored_revision_is_far_ahead()
    {
        SkipUnlessSupported();

        var id = Guid.NewGuid();
        await StoreAsync(id, "Acme", 0);
        await StoreAsync(id, "Acme, imported", 10);

        await StoreAsync(id, "Acme, after the jump", 0);

        (await StoredRevisionAsync(id)).ShouldBe(11);
        (await StoredCustomerAsync(id)).ShouldBe("Acme, after the jump");
    }
}

/// <summary>
/// Numeric revision semantics for a document implementing <see cref="IRevisioned" /> — what an
/// explicit revision <em>means</em> on the update path, settled by the maintainer ruling on
/// jasperfx#785 §4.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ruling: Marten's strictly-greater rule is the contract.</b> An explicit revision is the
/// version the caller is asking the document to <em>become</em>, and it is accepted only when it is
/// strictly greater than the revision currently stored. Revision <c>0</c> is the "auto" sentinel and
/// always wins, whatever is stored. Two of the three stores already work this way and say so in
/// their own source — Marten's guard is
/// <c>(? = 0 or {table}.{version} &lt; ?)</c> with the assignment
/// <c>CASE WHEN ? = 0 THEN {version} + 1 ELSE ? END</c>, and Fisher's <c>NumericRevision</c> is the
/// same pair, deliberately. Polecat diverged into an <em>equality</em> expectation
/// (<c>AND (? = 0 OR t.version = ?)</c>, assigning <c>ELSE ? + 1</c>) and is the side that changes.
/// </para>
/// <para>
/// The observable difference is one line of ordinary code: load a document at revision 5, store it
/// back still carrying 5. Under this ruling that is a <see cref="ConcurrencyException" />, not a
/// read-modify-write — 5 is not greater than 5, and the caller has to name 6. That is a sharp edge
/// and it is pinned deliberately rather than smoothed over, because the alternative silently
/// disagrees with the advice <see cref="ConcurrencyException.ToMessage" /> already ships ("You may
/// need to explicitly call <c>IDocumentSession.UpdateRevision()</c>"), which is written against the
/// strictly-greater model.
/// </para>
/// <para>
/// The capability the equality rule cannot express, and much of why the ruling went this way, is the
/// <b>non-contiguous jump</b>: an explicit revision may skip ahead (3 → 10), which is what lets a
/// caller adopt a revision decided somewhere else — an upstream version, a stream version, an
/// imported record. An equality rule can only ever step by one.
/// </para>
/// <para>
/// <b>The insert path follows the same rule</b> (ruled separately on jasperfx#785 after the update
/// path). An explicit revision on a brand-new document is honoured rather than discarded: the row
/// lands at exactly that revision, not at 1. Marten spells the insert value
/// <c>CASE WHEN ? = 0 THEN 1 ELSE ? END</c> and Fisher's <c>NumericRevision.InsertValueSql</c> is
/// <c>case when ? = 0 then 1 else ? end</c> — identical, and identical to the update assignment in
/// treating <c>?</c> as the target rather than something to increment past. Polecat hard-codes the
/// insert to <c>1</c> and is the side that changes (polecat#559).
/// </para>
/// <para>
/// Two neighbouring insert cases are deliberately <em>not</em> pinned, for opposite reasons:
/// </para>
/// <para>
/// An explicit revision of <b>zero</b> on a new document means auto and lands at 1 — but that is
/// already <see cref="NumericRevisionComplianceBase{TFixture,TDoc}.a_new_document_lands_at_revision_one" />,
/// which stores a document whose <c>Version</c> is the default <c>0</c>. It is the same assertion, so
/// it is not restated here; verified against both stores' <c>WHEN ? = 0 THEN 1</c> branch rather than
/// assumed from the update path's use of the same sentinel.
/// </para>
/// <para>
/// A <b>negative</b> revision is left unpinned because it is genuinely undefined rather than
/// divergent, and pinning it either way would invent a contract. No store guards against one — there
/// is no range check anywhere in Marten's, Fisher's or Polecat's revision handling — and none has a
/// test for it. Marten and Fisher would take the <c>ELSE ?</c> branch and store the negative
/// verbatim, after which the strictly-greater update guard refuses everything below it, so the row
/// sits at a revision no caller can have meant; Polecat's hard-coded <c>1</c> would swallow it. That
/// is three accidents, not two contracts and a bug. If a consumer ever needs an answer, the question
/// is which behavior to <em>choose</em>, and it should be ruled on before it is pinned.
/// </para>
/// <para>
/// <b>No seam members.</b> Every fact here runs through
/// <see cref="IDocumentWriteOperations.Store{T}" /> and <see cref="IRevisioned.Version" />, which is
/// the whole of the reachable surface: <c>Store(doc, revision)</c>, <c>UpdateRevision</c> and
/// <c>TryUpdateRevision</c> are product API and stay off the document contract (jasperfx#785 §5.3).
/// <c>Store</c> is enough because it passes the document's own <c>Version</c> as the expected
/// revision — the products' own docs put it as "<c>Store()</c> is essentially
/// <c>UpdateRevision(entity, entity.Version)</c>" — so setting <c>Version</c> before storing names
/// the revision. Gated on <see cref="DocumentStorageComplianceFixture.SupportsNumericRevisions" />
/// (default false) because numeric revisions are opt-in storage behavior, not one of the eight
/// contract operations.
/// </para>
/// <para>
/// The landed revision is read back off a freshly loaded document rather than off the instance that
/// was stored. Both routes work on the stores surveyed, but only the first is a statement about what
/// is <em>stored</em>; write-back onto the caller's instance is a separate convenience and pinning it
/// here would conflate the two. (It <em>is</em> pinned on the Guid side, where write-back is the fact
/// under test — see <see cref="GuidOptimisticConcurrencyCompliance{TFixture}" />.)
/// </para>
/// <para>
/// <b>The declaration route this suite cannot vary</b> is the other half, and it has its own suite:
/// <see cref="DeclaredNumericRevisionCompliance{TFixture}" /> runs these same nine facts against a
/// type that opted in through the store's own configuration instead of through the marker.
/// </para>
/// </remarks>
public abstract class NumericRevisionCompliance<TFixture>
    : NumericRevisionComplianceBase<TFixture, ComplianceLedgerEntry>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_revisions";
        config.AddDocumentType<ComplianceLedgerEntry>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    protected override ComplianceLedgerEntry NewDocument(Guid id, string customer, int version)
        => new() { Id = id, Customer = customer, Amount = 100, Version = version };

    protected override int RevisionOf(ComplianceLedgerEntry document) => document.Version;

    protected override string CustomerOf(ComplianceLedgerEntry document) => document.Customer;
}
