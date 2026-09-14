namespace JasperFx.Events.Vectors;

/// <summary>
///     How the text half of a hybrid search turns a caller's text into a query.
/// </summary>
/// <remarks>
///     <b>Both members are safe to hand a search box's raw contents, and that is why the list is
///     short.</b> A store's raw query syntax is deliberately absent: it can be malformed, and a
///     malformed query in one leg of a fused search fails the WHOLE call — where in a plain
///     <c>Where(x =&gt; x.Search(...))</c> it fails only the thing the caller asked for.
/// </remarks>
public enum HybridTextStyle
{
    /// <summary>Every word, in any order, with no query syntax at all. The default.</summary>
    PlainText,

    /// <summary>Quoted phrases, <c>or</c> between alternatives, a leading <c>-</c> to exclude.</summary>
    WebStyle
}

/// <summary>
///     Knobs for a hybrid search, shared by every store (jasperfx#840).
/// </summary>
/// <param name="K">
///     Reciprocal rank fusion's smoothing constant. 60 is the value the literature uses; lowering it
///     sharpens the advantage of a first-place finish.
/// </param>
/// <param name="CandidateDepth">
///     How deep each leg reads before fusing. Null means <c>max(limit × 4, 50)</c>, and it must be at
///     least <c>limit</c>: a document ranked 40th by one leg and 1st by the other is the result hybrid
///     search exists for, and reading only <c>limit</c> from each leg would never see it.
/// </param>
/// <param name="Distance">
///     ⚠️ <b>Null means "the metric the index declared", and that is the load-bearing default.</b>
///     Marten's own options defaulted this to <c>Cosine</c>, so an index declared <c>L2</c> was
///     searched by cosine while Polecat and Fisher searched by L2 — the same code, three stores,
///     different results, and nothing reported an error. Sharing the type is what makes that one
///     default rather than three.
/// </param>
/// <param name="TextStyle">How the text is turned into a query. See <see cref="HybridTextStyle" />.</param>
/// <param name="RegConfig">
///     The Postgres text-search configuration. Meaningful only on a Postgres-backed store; null means
///     the store's own default, and stores that have no such concept ignore it.
/// </param>
public sealed record HybridSearchOptions(
    int K = 60,
    int? CandidateDepth = null,
    DistanceFunction? Distance = null,
    HybridTextStyle TextStyle = HybridTextStyle.PlainText,
    string? RegConfig = null)
{
    /// <summary>The default candidate depth for a given limit.</summary>
    public static int DefaultCandidateDepth(int limit) => Math.Max(limit * 4, 50);

    /// <summary>
    ///     The candidate depth this search should use, with every refusal the stores were each making
    ///     for themselves.
    /// </summary>
    /// <remarks>
    ///     The option checks were copied into three stores with identical messages (jasperfx#844). One
    ///     copy means one set of rules, and a store cannot quietly stop applying one.
    /// </remarks>
    public int ResolveCandidateDepth(int limit)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1");
        }

        if (K < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(K), K,
                "K must be at least 1. It is reciprocal rank fusion's smoothing constant, and 0 would "
                + "make the top-ranked document of either leg score infinitely.");
        }

        var depth = CandidateDepth ?? DefaultCandidateDepth(limit);

        if (depth < limit)
        {
            throw new ArgumentOutOfRangeException(nameof(CandidateDepth), depth,
                $"CandidateDepth ({depth}) is below limit ({limit}), so the fusion would have fewer "
                + "candidates than it is asked to return. It exists to read DEEPER than limit: a "
                + "document ranked low by one leg and first by the other is what hybrid search is for.");
        }

        return depth;
    }
}

/// <summary>
///     A document and the fused score that ranked it.
/// </summary>
/// <remarks>
///     <b>Larger is better</b>, which is the opposite of <see cref="VectorMatch{T}" />'s distance — an
///     RRF score is a sum of reciprocals, so it rises with agreement between the legs. The absolute
///     value means little on its own; what it supports is a floor, or a comparison between results of
///     the same query.
/// </remarks>
public sealed record HybridMatch<T>(T Document, double Score);
