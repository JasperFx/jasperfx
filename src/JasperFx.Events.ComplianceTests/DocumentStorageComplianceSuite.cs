using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Base class for every shared document compliance suite. Owns the fixture lifecycle and forwards
/// the three seam members so the [Fact] bodies read like ordinary document store tests.
/// </summary>
/// <typeparam name="TFixture">The store's concrete document fixture.</typeparam>
/// <remarks>
/// A consuming store enrolls a suite with a single empty subclass:
/// <code>
/// public class document_query_compliance : DocumentQueryCompliance&lt;MyDocumentFixture&gt;;
/// </code>
/// </remarks>
public abstract class DocumentStorageComplianceSuite<TFixture> : IAsyncLifetime
    where TFixture : DocumentStorageComplianceFixture, new()
{
    protected readonly TFixture theFixture = new();

    /// <summary>
    /// The suite's standard store configuration. Implementations MUST return the same delegate
    /// instance every time (hold it in a static field) so the fixture can skip redundant rebuilds.
    /// </summary>
    protected abstract Action<DocumentComplianceConfig> Configuration { get; }

    public virtual async ValueTask InitializeAsync()
    {
        await theFixture.InitializeAsync().ConfigureAwait(false);
        await theFixture.ConfigureAsync(Configuration).ConfigureAwait(false);
        await theFixture.CleanDocumentDataAsync().ConfigureAwait(false);
    }

    public virtual ValueTask DisposeAsync() => theFixture.DisposeAsync();

    protected CancellationToken Cancellation => theFixture.Cancellation;

    /// <summary>
    /// Open a writable, committable session. Callers dispose it.
    /// </summary>
    protected IDocumentSessionOperations LightweightSession()
        => theFixture.Sessions.LightweightSession();

    /// <summary>
    /// Open a read-only session. Callers dispose it.
    /// </summary>
    protected IDocumentReadOperations QuerySession() => theFixture.Sessions.QuerySession();

    /// <summary>
    /// Store documents and commit them in one throwaway session — the setup step almost every test
    /// starts with.
    /// </summary>
    protected async Task PersistAsync<T>(params T[] documents) where T : notnull
    {
        await using var session = LightweightSession();
        session.Store(documents);
        await session.SaveChangesAsync(Cancellation).ConfigureAwait(false);
    }
}
