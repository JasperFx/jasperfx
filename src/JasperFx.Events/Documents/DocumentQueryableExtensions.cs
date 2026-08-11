using System.Linq.Expressions;

namespace JasperFx.Events.Documents;

/// <summary>
/// Store-agnostic asynchronous terminators for a document <see cref="IQueryable{T}" /> returned by
/// <see cref="IDocumentReadOperations.Query{T}" />.
/// </summary>
/// <remarks>
/// <para>
/// Extension methods rather than interface members, because the composable half of the query has to
/// be the real <see cref="IQueryable{T}" /> — real consumer code holds a queryable in a local and
/// adds <c>Where</c> / <c>OrderBy</c> / <c>Take</c> clauses to it across several statements, and
/// <c>System.Linq.Queryable</c>'s operators all return a plain <see cref="IQueryable{T}" />. Any
/// interface-member spelling of the terminators would be unreachable the moment a single standard
/// LINQ operator was applied.
/// </para>
/// <para>
/// Execution dispatches through <see cref="IDocumentQueryExecutor" />, which a store's LINQ provider
/// implements. That is the same design EF Core (<c>IAsyncQueryProvider</c>) and both Critter Stack
/// stores already use for their own terminators, so no store has to invent anything new to satisfy
/// it.
/// </para>
/// <para>
/// <strong>Name collision.</strong> These methods share their names and receiver type with each
/// store's own terminators (Marten's <c>Marten.QueryableExtensions</c>). A file that imports both
/// <c>JasperFx.Events.Documents</c> and <c>Marten</c> will get an ambiguity on
/// <c>ToListAsync</c> — neither candidate is better. This is why the document contract lives in its
/// own namespace rather than in the root <c>JasperFx.Events</c> namespace that consumers import
/// everywhere: the terminators are opt-in, and the code that opts in is precisely the code that has
/// no business importing a store. Where both are genuinely needed in one file, call the intended one
/// statically (<c>DocumentQueryableExtensions.ToListAsync(query, token)</c>).
/// </para>
/// <para>
/// The cancellation parameter is named <c>token</c> on every overload, matching both products.
/// Consumer code passes it by name; renaming it would be a source-breaking change.
/// </para>
/// </remarks>
public static class DocumentQueryableExtensions
{
    /// <summary>
    /// Execute the query and return every matching result.
    /// </summary>
    public static Task<IReadOnlyList<T>> ToListAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default)
    {
        return ExecutorFor(queryable).ExecuteToListAsync(queryable, token);
    }

    /// <summary>
    /// Execute the query and return the first result, or the default value when there are none.
    /// </summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default)
    {
        return ExecutorFor(queryable).ExecuteFirstOrDefaultAsync(queryable, token);
    }

    /// <summary>
    /// Execute the query, narrowed by an additional predicate, and return the first result or the
    /// default value when there are none.
    /// </summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default)
    {
        var narrowed = queryable.Where(predicate);
        return ExecutorFor(narrowed).ExecuteFirstOrDefaultAsync(narrowed, token);
    }

    /// <summary>
    /// Execute the query as a count of matching rows.
    /// </summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> queryable, CancellationToken token = default)
    {
        return ExecutorFor(queryable).ExecuteCountAsync(queryable, token);
    }

    /// <summary>
    /// Execute the query, narrowed by an additional predicate, as a count of matching rows.
    /// </summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default)
    {
        var narrowed = queryable.Where(predicate);
        return ExecutorFor(narrowed).ExecuteCountAsync(narrowed, token);
    }

    /// <summary>
    /// Execute the query as an existence check.
    /// </summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> queryable, CancellationToken token = default)
    {
        return ExecutorFor(queryable).ExecuteAnyAsync(queryable, token);
    }

    /// <summary>
    /// Execute the query, narrowed by an additional predicate, as an existence check.
    /// </summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default)
    {
        var narrowed = queryable.Where(predicate);
        return ExecutorFor(narrowed).ExecuteAnyAsync(narrowed, token);
    }

    /// <summary>
    /// Resolve the store's asynchronous execution hook for a queryable — its provider first, then the
    /// queryable itself.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when the queryable came from a LINQ provider that has not implemented
    /// <see cref="IDocumentQueryExecutor" /> — an in-memory <c>IEnumerable.AsQueryable()</c>, for
    /// instance. Silently falling back to synchronous, client-side evaluation would turn a missing
    /// implementation into a production performance bug rather than a compile-time-visible gap.
    /// </exception>
    internal static IDocumentQueryExecutor ExecutorFor<T>(IQueryable<T> queryable)
    {
        ArgumentNullException.ThrowIfNull(queryable);

        if (queryable.Provider is IDocumentQueryExecutor fromProvider)
        {
            return fromProvider;
        }

        if (queryable is IDocumentQueryExecutor fromQueryable)
        {
            return fromQueryable;
        }

        throw new NotSupportedException(
            $"The LINQ provider '{queryable.Provider.GetType().FullName}' does not implement {nameof(IDocumentQueryExecutor)}, so this query cannot be executed asynchronously. Queryables passed to {nameof(DocumentQueryableExtensions)} must originate from {nameof(IDocumentReadOperations)}.{nameof(IDocumentReadOperations.Query)}<T>() on a document store that supports the JasperFx document contract.");
    }
}
