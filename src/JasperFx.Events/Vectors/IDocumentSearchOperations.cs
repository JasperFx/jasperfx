using System.Linq.Expressions;

namespace JasperFx.Events.Vectors;

/// <summary>
///     The store-neutral similarity-search contract: vector search and hybrid search over a document
///     type, reachable without naming a concrete store (jasperfx#842).
/// </summary>
/// <remarks>
///     <para>
///         Every store's search entry point is an extension method on that store's own
///         <c>IQuerySession</c> that casts to store internals in its first statement, so code written
///         against <see cref="Documents.IDocumentReadOperations" /> had no way to ask for "the ten
///         nearest documents by embedding" at all. Under the rule jasperfx#665 applied, that is a
///         capability cliff rather than a convenience gap — the alternative is a per-store adapter,
///         which means either referencing all three store packages or keeping an adapter project per
///         store.
///     </para>
///     <para>
///         ⚠️ <b>A separate interface reached through
///         <see cref="Documents.IDocumentReadOperations.Search" />, deliberately NOT default members on
///         that interface.</b> The stores' existing extension methods are named
///         <c>VectorSearchWithScoresAsync</c> and <c>HybridSearchWithScoresAsync</c> already. Putting
///         members of those names on an interface a store's session implements would make the instance
///         member win overload resolution over the extension at every existing call site — silently,
///         with no error and different behavior, since the store's own extension is the one that knows
///         about its tenancy and soft-delete predicates. Keeping the surface behind an accessor makes
///         that collision impossible to have.
///     </para>
///     <para>
///         Scored methods are the contract and document-only ones are extensions over them
///         (<see cref="DocumentSearchExtensions" />), so an implementer writes two methods.
///     </para>
/// </remarks>
public interface IDocumentSearchOperations
{
    /// <summary>
    ///     The <paramref name="limit" /> documents nearest to <paramref name="query" />, nearest first,
    ///     each with its distance.
    /// </summary>
    /// <param name="member">The vector member, as <c>x =&gt; x.Embedding</c>.</param>
    /// <param name="query">The query vector, of the member's declared dimensions.</param>
    /// <param name="limit">How many documents to return.</param>
    /// <param name="distance">
    ///     Null means the metric the index declared, which is what a caller almost always wants: an
    ///     index built for one metric does not answer another one well.
    /// </param>
    /// <param name="filter">
    ///     An optional predicate, applied BEFORE <paramref name="limit" /> (jasperfx#843) — so the
    ///     result is the top-k of the filtered set rather than the filtered remains of the top-k.
    ///     Supports and refuses exactly what the store's own <c>Query&lt;T&gt;().Where(...)</c> does.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>Distance, never similarity, on every store and every metric: smaller is closer.</b>
    ///         See <see cref="VectorMatch{T}" />.
    ///     </para>
    ///     <para>
    ///         The store's own implicit predicates — conjoined tenancy, soft deletes, a document
    ///         hierarchy — apply as they would to <c>Query&lt;T&gt;()</c>. The
    ///         <paramref name="filter" /> is in addition to those, not instead of them.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>A store backed by an approximate index may return fewer than
    ///         <paramref name="limit" /> rows for a selective filter</b>, because the filter is applied
    ///         to what the index scan produced and that scan has a bound of its own (pgvector's
    ///         <c>hnsw.ef_search</c>, default 40). Stores doing an exact scan have no such bound. Each
    ///         store documents its own recall limits.
    ///     </para>
    /// </remarks>
    Task<IReadOnlyList<VectorMatch<T>>> VectorSearchWithScoresAsync<T>(
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit = 10,
        DistanceFunction? distance = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull;

    /// <summary>
    ///     A full-text ranking and a vector ranking of the same documents, fused by reciprocal rank
    ///     fusion, best first, each with its fused score.
    /// </summary>
    /// <param name="vectorMember">The vector member, as <c>x =&gt; x.Embedding</c>.</param>
    /// <param name="text">The text to search for. Safe to pass a search box's raw contents.</param>
    /// <param name="query">The same text already embedded, of the member's declared dimensions.</param>
    /// <param name="limit">How many documents to return.</param>
    /// <param name="options">Fusion and text-leg options; null takes every default.</param>
    /// <param name="filter">
    ///     An optional predicate, applied to BOTH legs before each leg's candidate depth — otherwise
    ///     rows the caller will discard consume the depth, and the fused order is a ranking of a set
    ///     that includes them (jasperfx#843).
    /// </param>
    /// <remarks>
    ///     <b>Larger is better here</b>, which is the opposite of
    ///     <see cref="VectorSearchWithScoresAsync{T}" />. See <see cref="HybridMatch{T}" /> and
    ///     <see cref="ReciprocalRankFusion" /> for what the score is and is not.
    /// </remarks>
    Task<IReadOnlyList<HybridMatch<T>>> HybridSearchWithScoresAsync<T>(
        Expression<Func<T, object?>> vectorMember,
        string text,
        ReadOnlyMemory<float> query,
        int limit = 10,
        HybridSearchOptions? options = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull;
}

/// <summary>
///     The document-only forms of <see cref="IDocumentSearchOperations" />, for callers that want the
///     matches and not the scores.
/// </summary>
/// <remarks>
///     Extensions rather than interface members so that an implementer writes the two scored methods
///     and nothing else — the projection to documents is identical for every store, and three copies
///     of it would be three chances to differ.
/// </remarks>
public static class DocumentSearchExtensions
{
    /// <inheritdoc cref="IDocumentSearchOperations.VectorSearchWithScoresAsync{T}" />
    public static async Task<IReadOnlyList<T>> VectorSearchAsync<T>(
        this IDocumentSearchOperations operations,
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit = 10,
        DistanceFunction? distance = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(operations);

        var matches = await operations
            .VectorSearchWithScoresAsync(member, query, limit, distance, filter, token)
            .ConfigureAwait(false);

        return matches.Select(x => x.Document).ToList();
    }

    /// <inheritdoc cref="IDocumentSearchOperations.HybridSearchWithScoresAsync{T}" />
    public static async Task<IReadOnlyList<T>> HybridSearchAsync<T>(
        this IDocumentSearchOperations operations,
        Expression<Func<T, object?>> vectorMember,
        string text,
        ReadOnlyMemory<float> query,
        int limit = 10,
        HybridSearchOptions? options = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(operations);

        var matches = await operations
            .HybridSearchWithScoresAsync(vectorMember, text, query, limit, options, filter, token)
            .ConfigureAwait(false);

        return matches.Select(x => x.Document).ToList();
    }
}
