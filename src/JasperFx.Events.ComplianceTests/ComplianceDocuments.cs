using System;
using JasperFx.Metadata;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// A document with a <see cref="Guid" /> identity — the default identity style in every Critter
/// Stack store.
/// </summary>
/// <remarks>
/// Deliberately a mutable POCO with a plain <c>Id</c> property, which is the one document shape all
/// three stores' identity conventions agree on. <c>[Identity]</c> attributes and non-conventional
/// identity members are product-specific configuration and stay out of scope for the document
/// contract; strong-typed identifiers came into scope with jasperfx#665 and have their own document
/// in <see cref="ComplianceCoupon" />.
/// </remarks>
public class ComplianceWidget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int Weight { get; set; }
}

/// <summary>
/// A document with a <see cref="string" /> identity, so the suites can hold both
/// <c>LoadAsync</c> / <c>Delete</c> identity overloads to the same definition.
/// </summary>
public class ComplianceGadget
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Weight { get; set; }
}

/// <summary>
/// A strong-typed identifier wrapping a <see cref="Guid" /> — the canonical shape all three stores'
/// value-type support agrees on.
/// </summary>
/// <remarks>
/// A <c>record struct</c> over a single positional member, which is what every store's value-type
/// detection looks for. Which primitive it wraps is a product concern and is not held to a shared
/// definition here; what is shared is that a document keyed by a wrapper must be loadable by that
/// wrapper.
/// </remarks>
public readonly record struct CouponCode(Guid Value);

/// <summary>
/// A document keyed by a strong-typed identifier, so the suites can hold
/// <see cref="Documents.IDocumentReadOperations.LoadAsync{T}(object,System.Threading.CancellationToken)" />
/// to a definition (jasperfx#665).
/// </summary>
/// <remarks>
/// The one compliance document whose type alone is not enough to configure a store: its identity
/// type has to be registered too, which is why <see cref="DocumentComplianceConfig.ValueTypes" />
/// exists. A suite using this document registers both.
/// </remarks>
public class ComplianceCoupon
{
    public CouponCode Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int PercentOff { get; set; }
}

/// <summary>
/// A document opting into numeric revisions through <see cref="IRevisioned" /> — the marker every
/// Critter Stack store already shares (it lives in <c>JasperFx</c>, not in any product), so a store
/// needs no compliance-specific configuration to recognize it.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ComplianceWidget" /> rather than a flag on it, because the marker is not
/// a property of an instance: implementing <see cref="IRevisioned" /> changes the storage the store
/// builds for the type, so the concurrency-checked and unchecked cases cannot share a document.
/// </para>
/// <para>
/// <c>Version</c> is the only member the numeric revision contract needs. It carries the expected
/// revision on the way in and the landed revision on the way out, which is what makes
/// <see cref="NumericRevisionCompliance{TFixture}" /> writable through
/// <see cref="Documents.IDocumentWriteOperations.Store{T}" /> alone — <c>UpdateRevision</c> and
/// <c>TryUpdateRevision</c> are product API and stay off the shared contract (jasperfx#785 §5.3).
/// </para>
/// </remarks>
public class ComplianceLedgerEntry: IRevisioned
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public int Amount { get; set; }

    /// <summary>
    /// The revision. Zero means "auto" on the way in; after a commit or a load it carries whatever
    /// the store has stored.
    /// </summary>
    public int Version { get; set; }
}

/// <summary>
/// The same numeric-revision document as <see cref="ComplianceLedgerEntry" />, declared the
/// <em>other</em> way — through the store's own <c>Schema.For&lt;T&gt;().UseNumericRevisions()</c>
/// rather than through the <see cref="IRevisioned" /> marker (jasperfx#819 §2).
/// </summary>
/// <remarks>
/// <para>
/// Pointedly <b>not</b> implementing <see cref="IRevisioned" />, which is the entire reason it is a
/// second type: <see cref="NumericRevisionCompliance{TFixture}" /> is written against the marker
/// throughout, so the one thing its nine facts cannot vary is <em>how the document says it uses
/// numeric revisions</em>. The two routes are documented as equivalent, and fisher#228 is what
/// happens when only one of them works — <c>Store(doc, revision)</c> and the operation's expected
/// revision were both gated on the marker, so a DSL-declared type had its revision dropped and
/// guarded on <c>0</c>, which means auto. A backwards write was accepted and nothing was reported:
/// a silent lost update, in the method whose entire purpose is to prevent one.
/// </para>
/// <para>
/// <c>Version</c> is spelled identically to the marker's member on purpose. Every store's
/// <c>UseNumericRevisions()</c> resolves a conventionally-named revision member, so keeping the name
/// means the only variable between the two suites is the declaration route — which is the variable
/// under test.
/// </para>
/// </remarks>
public class ComplianceMeterReading
{
    public Guid Id { get; set; }
    public string Meter { get; set; } = string.Empty;
    public int Reading { get; set; }

    /// <inheritdoc cref="ComplianceLedgerEntry.Version" />
    public int Version { get; set; }
}

/// <summary>
/// A document opting into <see cref="Guid" /> optimistic concurrency through
/// <see cref="IVersioned" /> — the marker that lives in <c>JasperFx.Metadata</c> rather than in any
/// product, lifted by jasperfx#330 and given the same status as <see cref="IRevisioned" />.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ComplianceLedgerEntry" /> for the same reason that one is separate from
/// <see cref="ComplianceWidget" />: the marker is not a property of an instance. Implementing it
/// changes the storage the store builds for the type, so the Guid-guarded, revision-guarded and
/// unguarded cases cannot share a document.
/// </para>
/// <para>
/// The suite that uses it declares it <em>twice</em> — the marker here and
/// <see cref="DocumentComplianceConfig.UseOptimisticConcurrency{T}" /> in the config — because the
/// stores disagree about whether the marker alone is an opt-in or merely supplies the member to
/// guard on. Declaring both leaves the suite testing the behavior rather than the opt-in route,
/// which is what it is for.
/// </para>
/// </remarks>
public class ComplianceShipment: IVersioned
{
    public Guid Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The store's version for this document. Carries the expected version on the way in and the
    /// landed version on the way out.
    /// </summary>
    public Guid Version { get; set; }
}
