using System.Linq.Expressions;

namespace JasperFx.Events.Documents;

/// <summary>
/// The mutating half of the store-agnostic document session contract — enlist documents into the
/// session's unit of work. Deliberately carries no commit: see
/// <see cref="IDocumentSessionOperations" />.
/// </summary>
/// <remarks>
/// <para>
/// Tier two of three. The split between "mutate" and "commit" is not decoration — it is the seam the
/// products already draw. Marten's <c>IDocumentOperations</c> has <c>Store</c> / <c>Delete</c> /
/// <c>DeleteWhere</c> but <em>not</em> <c>SaveChangesAsync</c>, which lives on
/// <c>IDocumentSession</c>. That is exactly why a projection's <c>RaiseSideEffects</c> receives
/// <c>IDocumentOperations</c>: a projection may write, but may not commit — the daemon owns the
/// transaction boundary. Collapsing the two tiers would hand projections a commit they must never
/// call.
/// </para>
/// <para>
/// Consequence worth stating plainly: the <c>TOperations</c> of the projection generics
/// (<see cref="Aggregation.JasperFxSingleStreamProjectionBase{TDoc,TId,TOperations,TQuerySession}" />,
/// Marten's <c>IDocumentOperations</c>) is this tier, while a consumer holding a committable session
/// wants the tier below it. The two are not the same type on Marten, so the document session
/// contract cannot simply reuse the projection generics' closure.
/// </para>
/// </remarks>
public interface IDocumentWriteOperations : IDocumentReadOperations
{
    /// <summary>
    /// Enlist one or more documents to be inserted or updated when the session is committed.
    /// </summary>
    void Store<T>(params T[] entities) where T : notnull;

    /// <summary>
    /// Enlist a document for deletion when the session is committed.
    /// </summary>
    void Delete<T>(T entity) where T : notnull;

    /// <summary>
    /// Enlist the document with this <see cref="Guid" /> identity for deletion when the session is
    /// committed. Deleting an identity that does not exist is a no-op rather than an error.
    /// </summary>
    void Delete<T>(Guid id) where T : notnull;

    /// <summary>
    /// Enlist the document with this <see cref="string" /> identity for deletion when the session is
    /// committed. Deleting an identity that does not exist is a no-op rather than an error.
    /// </summary>
    void Delete<T>(string id) where T : notnull;

    /// <summary>
    /// Enlist a criteria-based delete — every document of this type matching the expression is
    /// removed when the session is committed. Matching happens at commit time in the database, not
    /// against documents already loaded into the session.
    /// </summary>
    void DeleteWhere<T>(Expression<Func<T, bool>> expression) where T : notnull;
}
