using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using JasperFx.Linq;

namespace JasperFx.Documents;

/// <summary>
/// Applies <see cref="DocumentQueryOptions.Where" /> / <see cref="DocumentQueryOptions.OrderBy" /> to a
/// store's own <see cref="IQueryable{T}" /> through <see cref="DynamicQuery" />, and turns every way that can
/// fail into the contract's <see cref="DocumentCriteriaNotSupportedException" /> (jasperfx#869).
/// </summary>
/// <remarks>
/// <para>
/// The intended shape inside <see cref="IDocumentStoreDiagnostics.QueryDocumentsAsync" />:
/// </para>
/// <code>
/// var queryable = options.ApplyCriteriaTo(session.Query&lt;T&gt;(), tieBreaker: "Id", policy: MyStorePolicy);
/// try
/// {
///     total = await queryable.CountAsync(token);
///     page  = await queryable.Skip(...).Take(...).ToListAsync(token);
/// }
/// catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
/// {
///     throw options.Untranslatable(e);
/// }
/// </code>
/// <para>
/// Parse failures and allow-list refusals are thrown by <see cref="ApplyCriteriaTo{T}" />; a shape the
/// provider cannot translate only fails when the query runs, which is what <see cref="Untranslatable" />
/// is for. Either way the console gets a refusal rather than an unfiltered page.
/// </para>
/// </remarks>
public static class DocumentQueryCriteria
{
    /// <summary>Whether <paramref name="options" /> carries a predicate or an ordering.</summary>
    public static bool HasCriteria(this DocumentQueryOptions options)
        => !string.IsNullOrWhiteSpace(options.Where) || !string.IsNullOrWhiteSpace(options.OrderBy);

    /// <summary>
    /// Whether text criteria can be applied in this process at all. False under Native AOT, where
    /// <see cref="ApplyCriteriaTo{T}" /> refuses rather than trying.
    /// </summary>
    public static bool IsAvailable => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// <paramref name="source" /> with <see cref="DocumentQueryOptions.Where" /> and
    /// <see cref="DocumentQueryOptions.OrderBy" /> composed onto it, or <paramref name="source" /> unchanged
    /// when there are none.
    /// </summary>
    /// <param name="options">The query options.</param>
    /// <param name="source">The store's own queryable for the requested type, already scoped to the tenant.</param>
    /// <param name="tieBreaker">
    /// A member appended to a given ordering — the identity, normally — so that rows with equal keys still
    /// page deterministically. Ignored when there is no ordering.
    /// </param>
    /// <param name="policy">The store's policy — its size caps and the shapes it refuses.</param>
    /// <exception cref="DocumentCriteriaNotSupportedException">
    /// The text fails to parse or is refused (carrying <see cref="DocumentCriteriaNotSupportedException.Position" />
    /// for a parse failure), or the process cannot run dynamic code.
    /// </exception>
    [RequiresDynamicCode(DynamicQuery.AotMessage)]
    [RequiresUnreferencedCode(DynamicQuery.AotMessage)]
    public static IQueryable<T> ApplyCriteriaTo<T>(this DocumentQueryOptions options, IQueryable<T> source,
        string? tieBreaker = null, DynamicQueryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        if (!options.HasCriteria())
        {
            return source;
        }

        if (!IsAvailable)
        {
            throw new DocumentCriteriaNotSupportedException(firstCriterion(options),
                "property predicates and orderings are not available in a Native AOT process: they are translated with runtime code generation. Page without them, or narrow with IdEquals and the metadata filters.");
        }

        var orderBy = string.IsNullOrWhiteSpace(options.OrderBy) || string.IsNullOrWhiteSpace(tieBreaker)
            ? options.OrderBy
            : $"{options.OrderBy}, {tieBreaker}";

        try
        {
            return DynamicQuery.Apply(source, new DynamicQueryText(options.Where, orderBy, options.Arguments), policy);
        }
        catch (DynamicQueryException e)
        {
            throw new DocumentCriteriaNotSupportedException(criterionFor(e.Clause), e.Reason, e)
            {
                Position = e.Position
            };
        }
    }

    /// <summary>
    /// The refusal to throw when the store's provider could not translate the composed criteria — called
    /// from a <c>catch</c> around the query's execution.
    /// </summary>
    public static DocumentCriteriaNotSupportedException Untranslatable(this DocumentQueryOptions options,
        Exception providerFailure)
    {
        ArgumentNullException.ThrowIfNull(providerFailure);
        return new DocumentCriteriaNotSupportedException(firstCriterion(options),
            $"this store cannot translate it: {providerFailure.Message}", providerFailure);
    }

    /// <summary>
    /// Whether an exception thrown while <em>running</em> a criteria-bearing query is the provider refusing a
    /// shape — as opposed to the database being down, a timeout, or cancellation, which must propagate as
    /// themselves.
    /// </summary>
    public static bool IsTranslationFailure(Exception e)
        => e is BadLinqExpressionException or NotSupportedException or NotImplementedException
               or InvalidOperationException or ArgumentException or InvalidCastException or FormatException
           && e is not OperationCanceledException and not ObjectDisposedException;

    private static string firstCriterion(DocumentQueryOptions options)
        => string.IsNullOrWhiteSpace(options.Where) ? nameof(DocumentQueryOptions.OrderBy) : nameof(DocumentQueryOptions.Where);

    private static string criterionFor(DynamicQueryClause clause)
        => clause == DynamicQueryClause.OrderBy ? nameof(DocumentQueryOptions.OrderBy) : nameof(DocumentQueryOptions.Where);
}
