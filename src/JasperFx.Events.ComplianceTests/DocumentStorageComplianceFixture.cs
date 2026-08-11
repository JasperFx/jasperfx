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

    public virtual ValueTask InitializeAsync() => default;

    public virtual ValueTask DisposeAsync() => default;
}
