namespace JasperFx.Events.Documents;

/// <summary>
/// Opens document sessions without naming a concrete store. Binds to Marten's
/// <c>IDocumentStore</c> and Polecat's <c>IDocumentStore</c>, including their ancillary
/// (typed) store registrations.
/// </summary>
/// <remarks>
/// <para>
/// The non-generic form is the one that decouples a consumer: it hands back the shared session
/// contracts, so nothing downstream has to name <c>Marten.IDocumentSession</c> or its Polecat
/// counterpart. This mirrors how the event side already works — <see cref="IEventStore" /> is
/// non-generic and <see cref="IEventStore{TOperations,TQuerySession}" /> layers the concrete session
/// pair on top for store-generic infrastructure.
/// </para>
/// <para>
/// Tenant-scoped session opening is deliberately absent. The measured consumer surface opens no
/// tenant-scoped document sessions, and multi-tenancy beyond what <c>JasperFx.MultiTenancy</c>
/// already exposes is out of scope for this contract. Overloads taking a tenant id can be added
/// additively later.
/// </para>
/// </remarks>
public interface IDocumentSessionFactory
{
    /// <summary>
    /// Open a writable, committable session with no identity map — the cheap default for
    /// read-modify-write work.
    /// </summary>
    IDocumentSessionOperations LightweightSession();

    /// <summary>
    /// Open a read-only session for querying.
    /// </summary>
    IDocumentReadOperations QuerySession();
}

/// <summary>
/// The store-generic form of <see cref="IDocumentSessionFactory" />, handing back a product's own
/// session types rather than the shared contracts.
/// </summary>
/// <typeparam name="TOperations">
/// The product's committable session type — Marten <c>IDocumentSession</c>, Polecat
/// <c>IDocumentSession</c>.
/// </typeparam>
/// <typeparam name="TQuerySession">The product's read-only session type.</typeparam>
/// <remarks>
/// <para>
/// Exists for infrastructure that is itself generic over the session pair — the same reason
/// <see cref="IEventStore{TOperations,TQuerySession}" /> exists alongside <see cref="IEventStore" />.
/// Application code should prefer the non-generic interface; closing this one over a product's types
/// re-couples the caller to that product, which is the thing this contract is here to avoid.
/// </para>
/// <para>
/// Note that <typeparamref name="TOperations" /> here is the <em>committable</em> session, which on
/// Marten is a different type from the <c>TOperations</c> of the projection generics
/// (<c>IDocumentOperations</c>, which cannot commit). See <see cref="IDocumentWriteOperations" />.
/// </para>
/// </remarks>
public interface IDocumentSessionFactory<TOperations, TQuerySession> : IDocumentSessionFactory
    where TOperations : TQuerySession, IDocumentSessionOperations
    where TQuerySession : IDocumentReadOperations
{
    /// <inheritdoc cref="IDocumentSessionFactory.LightweightSession" />
    new TOperations LightweightSession();

    /// <inheritdoc cref="IDocumentSessionFactory.QuerySession" />
    new TQuerySession QuerySession();
}
