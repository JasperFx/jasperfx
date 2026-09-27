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
/// The tenant-scoped overloads were added later and additively (jasperfx#898), exactly as the
/// original remarks here said they could be. They carry throwing defaults, so a store that has no
/// conjoined document tenancy — or has not routed it through this contract yet — keeps compiling.
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

    /// <summary>
    /// Open a writable session scoped to one tenant. Every read and write through it is confined to
    /// <paramref name="tenantId" />, and every document it stores is stamped with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Added additively in jasperfx#898 rather than at the contract's birth, and the reason is worth
    /// recording. The original judgement — that the measured consumer surface opened no tenant-scoped
    /// document sessions — was about <em>application</em> code. What forced the overload was the
    /// compliance library: <c>DocumentConjoinedTenancyCompliance</c> cannot open a tenant-scoped
    /// session through any other route, and the document fixture's own rule is that a suite needing
    /// to reach past the interfaces means the contract has the hole, not the fixture.
    /// </para>
    /// <para>
    /// ⚠️ <b>A store implementing <see cref="IDocumentSessionFactory{TOperations,TQuerySession}" />
    /// has to write this one twice</b>, exactly as it already does for the parameterless pair: C#
    /// interface implementation is not return-type covariant, so a
    /// <c>public TOperations LightweightSession(string tenantId)</c> satisfies the generic member and
    /// leaves this one bound to the throwing default below. Add the one-line explicit forwarder —
    /// <c>IDocumentSessionOperations IDocumentSessionFactory.LightweightSession(string tenantId)
    /// =&gt; LightweightSession(tenantId);</c> — or the suites will fail on a store that is otherwise
    /// correct. This is the same near-miss <c>IDocumentReadOperations.Events</c> and
    /// <c>IDocumentSessionOperations.PendingStreams</c> already carry, and the same reason it is
    /// stated rather than left to be discovered.
    /// </para>
    /// </remarks>
    IDocumentSessionOperations LightweightSession(string tenantId)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement tenant-scoped document sessions.");

    /// <summary>
    /// Open a read-only session scoped to one tenant.
    /// </summary>
    /// <inheritdoc cref="LightweightSession(string)" path="/remarks" />
    IDocumentReadOperations QuerySession(string tenantId)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement tenant-scoped document sessions.");
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

    /// <inheritdoc cref="IDocumentSessionFactory.LightweightSession(string)" />
    new TOperations LightweightSession(string tenantId)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement tenant-scoped document sessions.");

    /// <inheritdoc cref="IDocumentSessionFactory.QuerySession(string)" />
    new TQuerySession QuerySession(string tenantId)
        => throw new NotSupportedException(
            $"{GetType().FullName} does not implement tenant-scoped document sessions.");
}
