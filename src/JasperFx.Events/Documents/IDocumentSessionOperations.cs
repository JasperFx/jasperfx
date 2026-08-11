namespace JasperFx.Events.Documents;

/// <summary>
/// A committable document session: everything <see cref="IDocumentWriteOperations" /> can enlist,
/// plus the transaction boundary that flushes it.
/// </summary>
/// <remarks>
/// Tier three of three, and the tier a store-agnostic consumer actually holds. Binds to Marten's
/// <c>IDocumentSession</c> and Polecat's <c>IDocumentSession</c>.
/// </remarks>
public interface IDocumentSessionOperations : IDocumentWriteOperations
{
    /// <summary>
    /// Commit every pending change enlisted in this session's unit of work as a single transaction.
    /// </summary>
    /// <remarks>
    /// The parameter is named <c>token</c> deliberately and must stay that way: both products spell
    /// it <c>token</c>, and real consumer code passes it by name.
    /// </remarks>
    Task SaveChangesAsync(CancellationToken token = default);
}
