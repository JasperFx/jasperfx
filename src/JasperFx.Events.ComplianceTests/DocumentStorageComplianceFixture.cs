using System;
using System.Threading;
using System.Threading.Tasks;
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

    public virtual ValueTask InitializeAsync() => default;

    public virtual ValueTask DisposeAsync() => default;
}
