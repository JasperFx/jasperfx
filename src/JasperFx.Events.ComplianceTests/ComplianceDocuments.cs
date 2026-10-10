// jasperfx#869: these documents compile inside the CONSUMER's test project (they ship as content files), and the
// criteria facts' SqlNullSemantics rule reads nullable-reference annotations to decide which members can be null.
// Without this directive a consumer that does not enable nullable compiles every string member here as
// unannotated — i.e. nullable — and the rule then refuses the shared facts' own `Name != @1`.
#nullable enable

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

/// <summary>
/// A document whose optimistic-concurrency version lives on a member of its own naming, declared
/// through <see cref="DocumentComplianceConfig.MapVersionTo{T}" /> rather than through
/// <see cref="IVersioned" /> (polecat#720).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately implements no marker interface.</b> The whole point of the mapped route is that
/// the member is named by configuration, so a type that also implemented <see cref="IVersioned" />
/// would let a store pass these facts off the marker and never consult the mapping — which is
/// exactly the bug the facts exist to catch.
/// </para>
/// <para>
/// <b>Why the shared suites needed this.</b>
/// <see cref="DocumentComplianceConfig.NumericRevisionTypes" />' remarks already record that
/// jasperfx#819 §2 could not run, because "the declared route has no document member" and every
/// revision fact works by setting one and reading it back. That was filed as a decision left open:
/// "what a suite would additionally need is a way to say which member is the revision". This is
/// that way, and polecat#720 is what came of not having it — the <b>fourth</b> independent sighting
/// of the same field (fisher#245, marten#5372, polecat#592, polecat#720), found each time by a
/// store rather than by the shared suite that exists to hold it.
/// </para>
/// <para>
/// <see cref="Etag" /> rather than <c>Version</c> on purpose: a store that quietly falls back to a
/// member <em>named</em> <c>Version</c> would pass these facts while ignoring the mapping it was
/// given, and that fallback is real — it is how more than one store resolves the numeric member
/// today.
/// </para>
/// </remarks>
public class CompliancePallet
{
    public Guid Id { get; set; }
    public string Carrier { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>The mapped concurrency version. Named by the configuration, not by any interface.</summary>
    public Guid Etag { get; set; }
}

/// <summary>
/// A document carrying both a full-text-searchable body and an embedding, so the search suites can
/// hold vector search, hybrid search and their filters to one definition (jasperfx#842).
/// </summary>
/// <remarks>
/// <para>
/// <b>One document rather than two, deliberately.</b> A hybrid search fuses a text ranking and a
/// vector ranking, and the shared contract's <c>HybridSearchWithScoresAsync</c> takes a single
/// document type — so a suite that split them could not call it at all. Fusing ACROSS types is
/// <see cref="Vectors.ReciprocalRankFusion" />'s job and is already covered by its own unit tests,
/// which need no store.
/// </para>
/// <para>
/// <b><see cref="Embedding" /> is <c>float[]</c>, which is the one vector member shape all three
/// stores accept.</b> The stores differ underneath — pgvector's <c>vector(n)</c>, SQL Server's
/// <c>VECTOR(n)</c>, Fisher's float32 blob — and each accepts more types than this. The contract
/// holds them only to the intersection.
/// </para>
/// <para>
/// <see cref="Scope" /> exists for jasperfx#843: the filter facts need a member whose value divides
/// the corpus so that the globally nearest rows can be made to belong entirely to the wrong scope.
/// </para>
/// </remarks>
public class ComplianceMemory
{
    public Guid Id { get; set; }

    /// <summary>The text the full-text half of a hybrid search reads.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>The partition a filter narrows to. See the remarks on the class.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The embedding the vector half of a search reads.</summary>
    public float[] Embedding { get; set; } = [];
}

/// <summary>
/// A document the diagnostics suite declares soft-deleted, so it can hold
/// <c>IDocumentStoreDiagnostics</c> to jasperfx#870's soft-delete semantics.
/// </summary>
/// <remarks>
/// Its own type rather than a flag on <see cref="ComplianceWidget" />: soft deletion is declared per
/// type (<see cref="DocumentComplianceConfig.SoftDeleted{T}" />) and changes the table the store builds,
/// so the soft-deleting and hard-deleting cases cannot share a document.
/// </remarks>
public class ComplianceTicket
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Priority { get; set; }
}

/// <summary>
/// The root of a document hierarchy — <see cref="ComplianceTruck" /> and <see cref="ComplianceBus" />
/// share its table — declared through <see cref="DocumentComplianceConfig.AddSubClass{TRoot,TSubClass}" />.
/// </summary>
public class ComplianceVehicle
{
    public Guid Id { get; set; }
    public string Make { get; set; } = string.Empty;
}

/// <summary>A sub-class stored in <see cref="ComplianceVehicle" />'s table.</summary>
public class ComplianceTruck : ComplianceVehicle
{
    public int Axles { get; set; }
}

/// <summary>A second sub-class, so "filter to the requested type" has something to exclude.</summary>
public class ComplianceBus : ComplianceVehicle
{
    public int Seats { get; set; }
}

/// <summary>
/// A sub-class of <see cref="ComplianceMemory" />, so the search suite can ask what a search for a
/// SUB-CLASS means over a shared hierarchy table (jasperfx#944, polecat#723, marten#5440).
/// </summary>
/// <remarks>
/// ⚠️ <b><see cref="Source" /> is what makes the bug observable, and a count assertion would miss
/// it.</b> A search returns its rows materialized as the <c>T</c> that was asked for, so a store
/// scanning the hierarchy table without a discriminator predicate does not merely include the
/// siblings — it hands them back AS this type, with this member at its default. Both found
/// instances of that (Polecat on all three search surfaces, Marten on vector search) were found
/// by reading rather than by a test, because every store's own search tests use a flat type.
/// </remarks>
public class ComplianceExcerpt : ComplianceMemory
{
    /// <summary>Set on every persisted excerpt; default on a row of the wrong type.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// A second sub-class of <see cref="ComplianceMemory" />, so "only the requested sub-class" has a
/// SIBLING to exclude and not merely the base type. The sibling is the sharper case: it is the one
/// the corpus puts NEAREST the query vector.
/// </summary>
public class ComplianceDigest : ComplianceMemory
{
    public string Period { get; set; } = string.Empty;
}
