namespace JasperFx.Events.Documents;

/// <summary>
/// The read half of the store-agnostic document session contract: load one document by identity,
/// or start a LINQ query over a document type.
/// </summary>
/// <remarks>
/// <para>
/// Tier one of three, mirroring the shape the event side already settled on
/// (<see cref="IQueryEventStore" /> → <see cref="IEventOperations" /> →
/// <see cref="IEventStoreOperations" />):
/// </para>
/// <list type="table">
///   <listheader><term>Tier</term><description>Marten / Polecat binding</description></listheader>
///   <item>
///     <term><see cref="IDocumentReadOperations" /></term>
///     <description>Marten <c>IQuerySession</c>, Polecat <c>IQuerySession</c></description>
///   </item>
///   <item>
///     <term><see cref="IDocumentWriteOperations" /></term>
///     <description>Marten <c>IDocumentOperations</c>, Polecat <c>IDocumentOperations</c></description>
///   </item>
///   <item>
///     <term><see cref="IDocumentSessionOperations" /></term>
///     <description>Marten <c>IDocumentSession</c>, Polecat <c>IDocumentSession</c></description>
///   </item>
/// </list>
/// <para>
/// This contract is deliberately tiny — it is the *document* slice a store-agnostic consumer needs
/// alongside <c>JasperFx.Events</c>, not a document store facade. Patching, bulk insert, LINQ joins /
/// grouping / <c>Include</c>, soft-delete semantics, document metadata, session listeners, the
/// stores' <c>Advanced</c> surfaces and schema management are all out of scope by design; a consumer
/// that needs those takes a dependency on a concrete store. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/647" />.
/// </para>
/// <para>
/// Identity overloads are limited to <see cref="Guid" /> and <see cref="string" /> — the two the
/// measured consumer surface uses. Numeric identities and tenant-scoped reads can be added
/// additively later without breaking any implementer.
/// </para>
/// </remarks>
public interface IDocumentReadOperations : IAsyncDisposable
{
    /// <summary>
    /// Load a single document by its <see cref="Guid" /> identity, or <see langword="null" /> when
    /// no document exists with that identity.
    /// </summary>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull;

    /// <summary>
    /// Load a single document by its <see cref="string" /> identity, or <see langword="null" /> when
    /// no document exists with that identity.
    /// </summary>
    Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull;

    /// <summary>
    /// Start a LINQ query over a document type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A genuine <see cref="IQueryable{T}" /> rather than a fluent builder, because real consumer
    /// code composes a query across statements — hold the queryable in a local, conditionally add
    /// <c>Where</c> clauses, then order and terminate it. Anything narrower would not compile
    /// against the call sites this contract exists to serve.
    /// </para>
    /// <para>
    /// The compliance contract covers the operators the measured consumer surface needs —
    /// <c>Where</c>, <c>Select</c>, <c>OrderBy</c> / <c>OrderByDescending</c>, <c>ThenBy</c>,
    /// <c>Take</c>, <c>Skip</c>, <c>Distinct</c> — terminated by the
    /// <see cref="DocumentQueryableExtensions" /> async methods. Everything else a store's LINQ
    /// provider happens to support stays a product concern and is deliberately not held to a shared
    /// definition; the stores' query languages diverge structurally enough that a broader shared
    /// suite would pin coincidence rather than contract.
    /// </para>
    /// <para>
    /// Products whose <c>Query&lt;T&gt;()</c> returns their own queryable interface (Marten's
    /// <c>IMartenQueryable&lt;T&gt;</c>) satisfy this with a one-line explicit implementation; C#
    /// interface implementation is not return-type covariant.
    /// </para>
    /// <para>
    /// Implementers that wrap another provider should note one <c>System.Linq</c> constraint:
    /// <c>Queryable.OrderBy</c> hard-casts the result of <c>IQueryProvider.CreateQuery&lt;T&gt;</c>
    /// to <see cref="IOrderedQueryable{T}" />, so a wrapper implementing only
    /// <see cref="IQueryable{T}" /> fails at the first <c>OrderBy</c>. Both current stores already
    /// satisfy this; it is stated because a new store's first wrapper usually does not.
    /// </para>
    /// </remarks>
    IQueryable<T> Query<T>() where T : notnull;
}
