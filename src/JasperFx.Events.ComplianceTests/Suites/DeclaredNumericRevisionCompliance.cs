using System;
using JasperFx.Events.Documents;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The nine numeric-revision facts again, against a document that declared itself through the
/// store's own configuration — <c>Schema.For&lt;T&gt;().UseNumericRevisions()</c> — rather than
/// through the <see cref="IRevisioned" /> marker (jasperfx#819 §2).
/// </summary>
/// <remarks>
/// <para>
/// One type, two documented-equivalent ways of saying the same thing, and a store where only one of
/// them did anything. That is fisher#228: <c>Store(doc, revision)</c> and the operation's expected
/// revision were both gated on the marker interface, so a DSL-declared type had its supplied
/// revision dropped and guarded on <c>0</c> — which means auto. A backwards write was accepted and
/// <c>TryUpdateRevision</c> dropped nothing: a silent lost update, in the method whose entire purpose
/// is to prevent one.
/// </para>
/// <para>
/// <see cref="NumericRevisionCompliance{TFixture}" /> could not have caught it, and Fisher's own
/// coverage did not either — the DSL test asserted the column existed and auto-incremented, and
/// stopped there. The declaration asymmetry is exactly the thing a shared suite is uniquely placed to
/// see, because it is invisible from inside either route on its own.
/// </para>
/// <para>
/// <b>Deliberately the same nine facts, not new ones.</b> Nothing here is a statement about revision
/// semantics — that is settled, once, in the base class. The only variable is the declaration route,
/// so adding a fact here that the marker suite does not carry would mean the two routes are
/// <em>not</em> equivalent, which is the thing being denied.
/// </para>
/// <para>
/// <b>Scoped to the implicit path.</b> jasperfx#785 §5.3 rules <c>Store(doc, revision)</c>,
/// <c>UpdateRevision</c> and <c>TryUpdateRevision</c> off the contract and says they should stay off,
/// and that is not reopened here — so the reachable half of fisher#228 is narrower than the bug was:
/// store → 1, store again → 2, stale → <see cref="ConcurrencyException" />, against a type that
/// declared itself the other way. The explicit-revision overloads stay product-tested.
/// </para>
/// <para>
/// <b>The fixture has to replay the config.</b>
/// <see cref="DocumentComplianceConfig.NumericRevisionTypes" /> is not optional here: a fixture that
/// drops it builds <see cref="ComplianceMeterReading" /> as an ordinary document with an <c>int</c>
/// property called <c>Version</c>, and then every fact fails for a reason that has nothing to do with
/// the store's revision handling. Gated on
/// <see cref="DocumentStorageComplianceFixture.SupportsNumericRevisions" /> — the same capability, a
/// second way of asking for it — rather than on a flag of its own, because a store that supports
/// numeric revisions and not this route is precisely the bug.
/// </para>
/// </remarks>
public abstract class DeclaredNumericRevisionCompliance<TFixture>
    : NumericRevisionComplianceBase<TFixture, ComplianceMeterReading>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_declared_revisions";
        config.AddDocumentType<ComplianceMeterReading>();
        config.UseNumericRevisions<ComplianceMeterReading>();
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    protected override ComplianceMeterReading NewDocument(Guid id, string customer, int version)
        => new() { Id = id, Meter = customer, Reading = 100, Version = version };

    protected override int RevisionOf(ComplianceMeterReading document) => document.Version;

    protected override string CustomerOf(ComplianceMeterReading document) => document.Meter;
}
