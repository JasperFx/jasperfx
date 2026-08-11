namespace JasperFx.Events.Documents;

/// <summary>
/// The asynchronous execution hook behind <see cref="DocumentQueryableExtensions" />. Implemented by
/// a store's LINQ <see cref="IQueryProvider" /> (or, failing that, by its queryable type).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IQueryable{T}" /> has no asynchronous execution path of its own, so every LINQ-capable
/// data access library invents one; Marten, Polecat and EF Core each did. This is the shared version
/// of that hook, and it is the only piece of the document contract a store must implement outside
/// its session types.
/// </para>
/// <para>
/// Four primitives, no predicate overloads: the predicate forms on
/// <see cref="DocumentQueryableExtensions" /> compose <c>Queryable.Where</c> before dispatching
/// here, so adding them costs an implementer nothing.
/// </para>
/// <para>
/// The hook hangs off the provider rather than the queryable because <see cref="IQueryable.Provider" />
/// is preserved by definition across every LINQ operator, whereas the queryable's own type is only
/// preserved by convention. <see cref="DocumentQueryableExtensions" /> checks the queryable as a
/// fallback for stores that find that more natural.
/// </para>
/// </remarks>
public interface IDocumentQueryExecutor
{
    /// <summary>
    /// Execute the query and return every matching result.
    /// </summary>
    Task<IReadOnlyList<T>> ExecuteToListAsync<T>(IQueryable<T> queryable, CancellationToken token);

    /// <summary>
    /// Execute the query and return the first result, or the default value when there are none.
    /// </summary>
    Task<T?> ExecuteFirstOrDefaultAsync<T>(IQueryable<T> queryable, CancellationToken token);

    /// <summary>
    /// Execute the query as a count of matching rows.
    /// </summary>
    Task<int> ExecuteCountAsync<T>(IQueryable<T> queryable, CancellationToken token);

    /// <summary>
    /// Execute the query as an existence check.
    /// </summary>
    Task<bool> ExecuteAnyAsync<T>(IQueryable<T> queryable, CancellationToken token);
}
