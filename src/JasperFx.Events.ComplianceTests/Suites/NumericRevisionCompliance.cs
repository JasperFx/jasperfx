using System;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

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
/// here would conflate the two.
/// </para>
/// </remarks>
public abstract class NumericRevisionCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_revisions";
        config.AddDocumentType<ComplianceLedgerEntry>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    private void SkipUnlessSupported()
    {
        Assert.SkipUnless(theFixture.SupportsNumericRevisions,
            "This document store does not implement numeric revisions");
    }

    /// <summary>
    /// Store one ledger entry at the named revision, committing in its own session. A fresh instance every
    /// time so a store that writes the landed revision back onto the caller's document cannot leak
    /// state from one step of a test into the next.
    /// </summary>
    private async Task StoreAsync(Guid id, string customer, int version)
    {
        await using var session = LightweightSession();
        session.Store(new ComplianceLedgerEntry { Id = id, Customer = customer, Amount = 100, Version = version });
        await session.SaveChangesAsync(Cancellation);
    }

    private async Task<ConcurrencyException> StoreShouldBeRefusedAsync(Guid id, string customer, int version)
        => await Should.ThrowAsync<ConcurrencyException>(() => StoreAsync(id, customer, version));

    /// <summary>
    /// The stored revision, read through the marker interface on a freshly loaded document — the only
    /// route the shared contract offers, and the one that is a statement about storage.
    /// </summary>
    private async Task<int> StoredRevisionAsync(Guid id)
    {
        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceLedgerEntry>(id, Cancellation);
        loaded.ShouldNotBeNull();
        return loaded.Version;
    }

    private async Task<string> StoredCustomerAsync(Guid id)
    {
        await using var query = QuerySession();
        var loaded = await query.LoadAsync<ComplianceLedgerEntry>(id, Cancellation);
        loaded.ShouldNotBeNull();
        return loaded.Customer;
    }

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
