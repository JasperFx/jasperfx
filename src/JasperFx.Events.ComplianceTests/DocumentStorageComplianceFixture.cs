using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Documents;
using JasperFx.Events.Documents;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// The seam between the shared document compliance suites and a concrete store.
/// </summary>
/// <remarks>
/// <para>
/// Three members wide, and unlike
/// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}" /> it is <em>not</em> generic
/// over the store's session pair. That asymmetry is the headline result of jasperfx#647 rather than
/// an inconsistency: the event compliance fixture has to be generic because so much of the event
/// surface is only reachable through a product's own session type, whereas everything the document
/// suites do runs through <see cref="IDocumentSessionFactory" /> and the three session contracts. If
/// a document suite ever needs a fixture member to reach past those interfaces, that is evidence the
/// contract has a hole — fix the contract, do not widen this seam.
/// </para>
/// <para>
/// A store that also implements <see cref="IDocumentSessionFactory{TOperations,TQuerySession}" />
/// proves that at compile time in its own fixture; nothing here needs to know.
/// </para>
/// </remarks>
public abstract class DocumentStorageComplianceFixture : IAsyncLifetime
{
    private Action<DocumentComplianceConfig>? _lastConfiguration;

    /// <summary>
    /// Cancellation token handed to every store call the suites make. Overridable rather than
    /// hard-coded so a consumer can swap in its own budget.
    /// </summary>
    public virtual CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>
    /// Build (or rebuild) the store for the supplied configuration.
    /// </summary>
    /// <remarks>
    /// Keyed on the identity of the <paramref name="configure" /> delegate exactly as
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.ConfigureAsync" /> is:
    /// suites hold their standard configuration in a static field, so repeated calls across the test
    /// methods of one class are free.
    /// </remarks>
    public async Task ConfigureAsync(Action<DocumentComplianceConfig> configure)
    {
        if (ReferenceEquals(_lastConfiguration, configure))
        {
            return;
        }

        var config = new DocumentComplianceConfig();
        configure(config);

        await BuildStoreAsync(config).ConfigureAwait(false);

        _lastConfiguration = configure;
    }

    /// <summary>
    /// Construct the store from the store-neutral configuration and make sure its schema exists.
    /// </summary>
    protected abstract Task BuildStoreAsync(DocumentComplianceConfig config);

    /// <summary>
    /// The store, as the shared session-opening contract. The only member the suites read state
    /// through.
    /// </summary>
    public abstract IDocumentSessionFactory Sessions { get; }

    /// <summary>
    /// Per-test isolation: remove all document data without dropping schema.
    /// </summary>
    /// <remarks>
    /// Not expressible through the contract on purpose — wiping a store is administration, and
    /// <see cref="IDocumentWriteOperations.DeleteWhere{T}" /> would only cover document types the
    /// suite already knows about. Both products spell this on their own <c>Advanced</c> surface,
    /// which the contract deliberately does not abstract.
    /// </remarks>
    public abstract Task CleanDocumentDataAsync();

    /// <summary>
    /// Does this store implement numeric revisions — the <see cref="IRevisioned" /> marker and the
    /// concurrency guard behind it? Gates <see cref="NumericRevisionCompliance{TFixture}" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A capability flag, not a seam: it is <c>virtual</c> rather than <c>abstract</c>, so the three
    /// abstract members stay three and every existing fixture keeps compiling untouched. The suite it
    /// gates reaches the whole behavior through <see cref="IDocumentWriteOperations.Store{T}" /> and
    /// <see cref="IRevisioned.Version" />, so nothing here reaches past the contract — which is
    /// exactly the bar the class-level remarks set.
    /// </para>
    /// <para>
    /// Default <b>false</b>, following the established pattern, because numeric revisions are opt-in
    /// storage behavior rather than part of the eight-operation contract: a store can implement the
    /// document contract completely and stamp no revisions at all. Flip it after implementing them.
    /// </para>
    /// </remarks>
    public virtual bool SupportsNumericRevisions => false;

    /// <summary>
    /// Does this store implement <see cref="Guid" /> optimistic concurrency — the
    /// <see cref="JasperFx.Metadata.IVersioned" /> marker, the
    /// <see cref="DocumentComplianceConfig.UseOptimisticConcurrency{T}" /> declaration, and the guard
    /// behind them? Gates <see cref="GuidOptimisticConcurrencyCompliance{TFixture}" /> (jasperfx#819).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A capability flag, not a seam, mirroring <see cref="SupportsNumericRevisions" /> exactly: the
    /// suite it gates reaches the whole behavior through
    /// <see cref="IDocumentWriteOperations.Store{T}" />,
    /// <see cref="IDocumentWriteOperations.Update{T}" />, <c>LoadAsync</c> and
    /// <see cref="JasperFx.Metadata.IVersioned.Version" />, so nothing reaches past the contract.
    /// </para>
    /// <para>
    /// Default <b>false</b> for the same reason: optimistic concurrency is opt-in storage behavior, not
    /// one of the eight contract operations. Flip it after implementing it — and note that the
    /// fixture must also replay
    /// <see cref="DocumentComplianceConfig.OptimisticConcurrencyTypes" />, which is not optional on a
    /// store where the marker alone is not the opt-in.
    /// </para>
    /// </remarks>
    public virtual bool SupportsOptimisticConcurrency => false;

    /// <summary>
    /// Does this store implement <see cref="Vectors.IDocumentSearchOperations.VectorSearchWithScoresAsync{T}" />,
    /// reached through <see cref="IDocumentReadOperations.Search" />? Gates
    /// <see cref="DocumentSearchCompliance{TFixture}" /> (jasperfx#842).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default <b>false</b>, and it has to be: <see cref="IDocumentReadOperations.Search" /> carries a
    /// throwing default, so a store that has not implemented search is a store that compiles and
    /// throws — exactly the shape a capability flag is for. Flip it after implementing the member,
    /// and note the fixture must also replay
    /// <see cref="DocumentComplianceConfig.VectorIndexes" />: a vector search reads a DECLARED index,
    /// so a fixture that ignores the declaration fails every fact rather than skipping.
    /// </para>
    /// <para>
    /// ⚠️ <b>A store backed by an APPROXIMATE index should read the filter facts before flipping
    /// this.</b> They assert that a predicate excluding every globally-nearest row still returns the
    /// full limit, which an exact scan gives for free and an HNSW index does not: pgvector applies
    /// the predicate after an index scan bounded by <c>hnsw.ef_search</c>. That is a real difference
    /// between the stores rather than a bug in any of them, and it is why the corpus these facts use
    /// is kept small enough to sit inside any sane bound.
    /// </para>
    /// </remarks>
    public virtual bool SupportsVectorSearch => false;

    /// <summary>
    /// Does this store implement <see cref="Vectors.IDocumentSearchOperations.HybridSearchWithScoresAsync{T}" />?
    /// Gates the hybrid facts of <see cref="DocumentSearchCompliance{TFixture}" />.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SupportsVectorSearch" /> because the two capabilities genuinely come
    /// apart: hybrid search additionally needs a full-text index, which a store may not have at all
    /// (or may have only for some member types), while vector search needs none. A fixture flipping
    /// this must also replay <see cref="DocumentComplianceConfig.FullTextIndexes" />.
    /// </remarks>
    public virtual bool SupportsHybridSearch => false;

    /// <summary>
    /// Does this store slice documents by tenant within one database — conjoined document tenancy —
    /// and route it through <see cref="IDocumentSessionFactory.LightweightSession(string)" /> /
    /// <see cref="IDocumentSessionFactory.QuerySession(string)" />? Gates
    /// <see cref="DocumentConjoinedTenancyCompliance{TFixture}" /> (jasperfx#898).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default <b>false</b>, and for two reasons rather than the usual one. The tenant-scoped session
    /// overloads are additive members with throwing defaults, so a store that has not written them is
    /// a store that compiles and throws. And the replay half —
    /// <see cref="DocumentComplianceConfig.ConjoinedDocuments" /> — is not optional on a store where
    /// documents default to single-tenanted, which is all three of them.
    /// </para>
    /// <para>
    /// ⚠️ <b>Read the covariance note on
    /// <see cref="IDocumentSessionFactory.LightweightSession(string)" /> before flipping this.</b> A
    /// store implementing the generic session factory has to write the explicit non-generic forwarder
    /// as well, or the suites reach the throwing default on a store whose tenancy is perfectly
    /// correct.
    /// </para>
    /// </remarks>
    public virtual bool SupportsConjoinedDocuments => false;

    /// <summary>
    /// Does this store offer the two deliberate escapes from tenant scoping — read every tenant's
    /// rows, and read a named set of tenants' rows? Gates the cross-tenant facts of
    /// <see cref="DocumentConjoinedTenancyCompliance{TFixture}" />.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SupportsConjoinedDocuments" /> because the two genuinely come apart: a
    /// store can scope every read to the session's tenant — which is the whole isolation contract —
    /// and offer no way to deliberately step outside it. Default <b>false</b>; flip it after
    /// implementing <see cref="QueryAllTenantsAsync{T}" /> and <see cref="QueryTenantsAsync{T}" />.
    /// </remarks>
    public virtual bool SupportsCrossTenantQueries => false;

    /// <summary>
    /// Every <typeparamref name="T" /> in the store, across every tenant — the store's own
    /// <c>AnyTenant</c> escape, executed and materialized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seam member because the spelling genuinely differs in kind, not merely in name. Marten
    /// writes it as an <em>element predicate</em> inside the <c>Where</c>
    /// (<c>Query&lt;T&gt;().Where(x =&gt; x.AnyTenant())</c>, recognized by the LINQ parser from the
    /// method's declaring type), while Polecat and Fisher write it as an <em>operator on the
    /// queryable</em> (<c>Query&lt;T&gt;().AnyTenant()</c>). Neither form can be written once: the
    /// predicate form carries the wrong <c>MethodInfo</c> if it is built in shared source, and the
    /// operator form is an extension in each product's own namespace. Same reasoning as
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.HasTagFilter{TTag}" />.
    /// </para>
    /// <para>
    /// Deliberately predicate-free and materialized, on the same reasoning as
    /// <see cref="EventStoreComplianceFixture{TOperations,TQuerySession}.QueryTableAsync" />: what is
    /// under test is the tenant scope the escape lifts, not any provider's operator set. The moment
    /// this hands back a queryable it starts pinning LINQ surface the library keeps out of scope
    /// permanently.
    /// </para>
    /// </remarks>
    public virtual Task<IReadOnlyList<T>> QueryAllTenantsAsync<T>(
        IDocumentReadOperations session, CancellationToken token) where T : notnull
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement QueryAllTenantsAsync, so it cannot run the cross-tenant document compliance facts.");

    /// <summary>
    /// Every <typeparamref name="T" /> belonging to any of <paramref name="tenantIds" /> — the
    /// store's own <c>TenantIsOneOf</c> escape, executed and materialized.
    /// </summary>
    /// <inheritdoc cref="QueryAllTenantsAsync{T}" path="/remarks" />
    public virtual Task<IReadOnlyList<T>> QueryTenantsAsync<T>(
        IDocumentReadOperations session, string[] tenantIds, CancellationToken token) where T : notnull
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement QueryTenantsAsync, so it cannot run the cross-tenant document compliance facts.");

    /// <summary>
    /// Does this store implement <see cref="IDocumentStoreDiagnostics" />? Gates
    /// <see cref="DocumentStoreDiagnosticsCompliance{TFixture}" /> (jasperfx#870).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A new seam, and deliberately so rather than a hole in the session contract: the diagnostics
    /// surface is a <em>separate</em> contract — the one a console reaches through DI, by type name and
    /// raw JSON — so there is nothing on <see cref="Sessions" /> it could be reached through. Flip this
    /// and override <see cref="DocumentDiagnostics" /> together.
    /// </para>
    /// <para>
    /// The suite writes its setup data through <see cref="Sessions" /> and reads it back through
    /// <see cref="DocumentDiagnostics" />, so the two must be views of the same store.
    /// </para>
    /// </remarks>
    public virtual bool SupportsDocumentDiagnostics => false;

    /// <summary>
    /// The store's <see cref="IDocumentStoreDiagnostics" />, over the store <see cref="Sessions" /> opens.
    /// </summary>
    public virtual IDocumentStoreDiagnostics DocumentDiagnostics
        => throw new NotSupportedException(
            $"{GetType().FullName} does not expose IDocumentStoreDiagnostics, so it cannot run the document diagnostics compliance facts.");

    /// <summary>
    /// Can this store's <see cref="IDocumentStoreDiagnostics" /> apply
    /// <see cref="DocumentQueryOptions.Where" /> and <see cref="DocumentQueryOptions.OrderBy" />?
    /// </summary>
    /// <remarks>
    /// Not a skip gate in the usual sense. Left <b>false</b>, the criteria facts are replaced by the
    /// refusal fact — a store without predicate support must throw
    /// <see cref="DocumentCriteriaNotSupportedException" /> rather than return the unfiltered page, and
    /// that is asserted, not skipped. Flip it once the store applies them (jasperfx#869).
    /// </remarks>
    public virtual bool SupportsDocumentDiagnosticCriteria => false;

    /// <summary>
    /// Does this store's <see cref="IDocumentStoreDiagnostics" /> honour
    /// <see cref="DocumentQueryOptions.AllTenants" /> (jasperfx#928)?
    /// </summary>
    /// <remarks>
    /// A fork, like <see cref="SupportsDocumentDiagnosticCriteria" />. Left <b>false</b> on a store with
    /// <see cref="SupportsConjoinedDocuments" />, the suite asserts that an all-tenants read is
    /// <b>refused</b> with <see cref="DocumentCriteriaNotSupportedException" /> — returning the default
    /// tenant's rows as though they were every tenant's is the one wrong answer. Flip it once the store
    /// reads every tenant and the all-tenants facts run instead.
    /// </remarks>
    public virtual bool SupportsDocumentDiagnosticAllTenants => false;

    /// <summary>
    /// Does this store implement <see cref="IDocumentStoreDiagnosticsWriter" />? Gates the write facts
    /// of <see cref="DocumentStoreDiagnosticsCompliance{TFixture}" />. Flip this and override
    /// <see cref="DocumentDiagnosticsWriter" /> together.
    /// </summary>
    public virtual bool SupportsDocumentDiagnosticWrites => false;

    /// <summary>
    /// The store's <see cref="IDocumentStoreDiagnosticsWriter" />, over the store <see cref="Sessions" />
    /// opens.
    /// </summary>
    public virtual IDocumentStoreDiagnosticsWriter DocumentDiagnosticsWriter
        => throw new NotSupportedException(
            $"{GetType().FullName} does not expose IDocumentStoreDiagnosticsWriter, so it cannot run the document diagnostics write facts.");

    /// <summary>
    /// Does this fixture replay <see cref="DocumentComplianceConfig.SoftDeletedDocuments" />? Gates the
    /// soft-delete facts of <see cref="DocumentStoreDiagnosticsCompliance{TFixture}" />.
    /// </summary>
    /// <remarks>
    /// Every current store supports soft deletes; the flag exists because no fixture replayed the
    /// declaration before jasperfx#870, and a fixture that does not replay it would fail those facts for
    /// a wiring reason.
    /// </remarks>
    public virtual bool SupportsSoftDeletedDocuments => false;

    /// <summary>
    /// Does this fixture replay <see cref="DocumentComplianceConfig.SubClasses" />? Gates the hierarchy
    /// facts of <see cref="DocumentStoreDiagnosticsCompliance{TFixture}" />, on the same reasoning as
    /// <see cref="SupportsSoftDeletedDocuments" />.
    /// </summary>
    public virtual bool SupportsDocumentHierarchies => false;

    public virtual ValueTask InitializeAsync() => default;

    public virtual ValueTask DisposeAsync() => default;
}
