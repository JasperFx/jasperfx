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

    /// <summary>
    /// Enlist a document to be inserted when the session is committed. Unlike
    /// <see cref="Store{T}(T[])" /> the commit fails if the identity already exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single document because that is the call every consumer makes and the shape Polecat and Fisher
    /// declare, so a session on either satisfies this with no change. Marten declares the
    /// <c>params</c> and <see cref="IEnumerable{T}" /> forms instead and needs a one-line explicit
    /// implementation. See <see href="https://github.com/JasperFx/jasperfx/issues/1052" />.
    /// </para>
    /// <para>
    /// The default cannot fall back to <see cref="Store{T}(T[])" />: an upsert would turn the duplicate this
    /// call exists to refuse into a quiet overwrite.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation only, when the store has not implemented this member.
    /// </exception>
    void Insert<T>(T document) where T : notnull
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IDocumentWriteOperations)}.{nameof(Insert)}. This member deliberately throws rather than falling back to {nameof(Store)}, which would overwrite the existing document this call has to refuse.");

    /// <summary>
    /// Enlist a document to be updated when the session is committed. Unlike
    /// <see cref="Store{T}(T[])" /> the commit fails if the identity does not exist.
    /// </summary>
    /// <remarks>
    /// The exception raised for a missing document is the store's own, because Marten's derives from
    /// <c>MartenException</c>. The single-document shape has the same reasoning as
    /// <see cref="Insert{T}" />.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation only, when the store has not implemented this member.
    /// </exception>
    void Update<T>(T document) where T : notnull
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IDocumentWriteOperations)}.{nameof(Update)}. This member deliberately throws rather than falling back to {nameof(Store)}, which would create the missing document this call has to refuse.");

    /// <summary>
    /// Enlist documents of mixed types to be inserted or updated when the session is committed, each as its
    /// own runtime type.
    /// </summary>
    /// <remarks>
    /// Marten and Polecat declare this already, so a session on either satisfies it with no change. Fisher
    /// does not, and Wolverine reflects over <c>Store&lt;T&gt;</c> to do the same thing, which is the
    /// reason it is here.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation only, when the store has not implemented this member.
    /// </exception>
    void StoreObjects(IEnumerable<object> documents)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IDocumentWriteOperations)}.{nameof(StoreObjects)}.");

    /// <summary>
    /// This session's tenant, and the causation, correlation and user metadata stamped onto what it writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An accessor, and not this interface deriving from <see cref="IMetadataContext" />, which is the
    /// shape the issue first asked for and does not compile for callers. All three stores declare
    /// <c>TenantId</c> on an <c>IQuerySession</c> that does not derive from <see cref="IMetadataContext" />,
    /// so a store's <c>IDocumentSession</c> would inherit two unrelated <c>TenantId</c>s and every
    /// <c>session.TenantId</c> in every application would be CS0229. See
    /// <see href="https://github.com/JasperFx/jasperfx/issues/1052" />.
    /// </para>
    /// <para>
    /// The default answers for every store as it stands: all three session classes already implement
    /// <see cref="IMetadataContext" /> through Weasel's <c>IStorageSession</c>, and only the public
    /// interfaces leave it out, so this closes a naming gap. <c>CurrentUserName</c> is the spelling to use.
    /// It is the same state as <c>LastModifiedBy</c> on all three.
    /// </para>
    /// <para>
    /// On the write tier so that a subscription, which holds this tier and must not commit, can read its
    /// tenant.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation only, when the session is not an <see cref="IMetadataContext" />.
    /// </exception>
    IMetadataContext MetadataContext
        => this as IMetadataContext ?? throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IMetadataContext)}, so its tenant and unit-of-work metadata cannot be reached through {nameof(IDocumentWriteOperations)}.");

    /// <summary>
    /// Have <paramref name="participant" /> write inside the transaction that commits this unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the write tier because a subscription's outbox has to enlist in the daemon's transaction, and
    /// enlisting is not committing: this tier is the one a subscription and a projection are allowed.
    /// The same-named members on the stores take their own driver's participant type, so the two overloads
    /// never meet. See <see href="https://github.com/JasperFx/jasperfx/issues/1052" />.
    /// </para>
    /// <para>
    /// ⚠️ <strong>A store must throw here for any session whose commit it does not run.</strong> The default
    /// throws for a store with no shared participants. A store that accepts the call and then
    /// does not run the participant loses whatever it was going to write, and the three shapes that do that
    /// today are all quiet: Marten's <c>AmbientTransactionLifetime</c> runs no participants at all, Marten's
    /// <c>ProjectionDocumentSession</c> drops one when its work tracker is not a projection batch, and a
    /// session enlisted in a caller's transaction never learns the commit landed. A participant is also
    /// real work: see <see cref="DocumentSessionHooks.HasPendingParticipants" />.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// From the default implementation, or from a store on a session that cannot honour the enlistment.
    /// </exception>
    void AddTransactionParticipant(IDbTransactionParticipant participant)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement {nameof(IDbTransactionParticipant)} enlistment, so it refuses this participant here rather than accepting work it would never run.");
}
