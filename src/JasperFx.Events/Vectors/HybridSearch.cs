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
/// <param name="ColumnWeights">
///     Per-column weights for the text leg's relevance ranking, in the order the full-text index
///     declared its members (jasperfx#854, for fisher#289). Null — the default — weighs every column
///     the same.
///     <para>
///         <b>The text leg decides which candidates exist at all</b>, which is why this belongs on the
///         options rather than being left to a caller's own ordering. RRF fuses <em>ranks</em>, so the
///         text leg's order picks who survives <c>CandidateDepth</c> and how much each survivor
///         contributes. A store with a title column and a body column cannot express "a title hit
///         outweighs a body hit" through a hybrid search without this.
///     </para>
///     <para>
///         ⚠️ <b>Honoured only by a store that ranks per column at query time.</b> That is Fisher, whose
///         <c>bm25()</c> takes one weight per indexed column. Marten weights at INDEX time through
///         <c>WeightedFullTextIndex</c>, and Polecat's full-text ranking addresses a single member, so
///         neither has anywhere to put these. Both refuse a non-null value by name rather than
///         ignoring it — see <see cref="AssertColumnWeightsAreNotSupported" />.
///     </para>
///     <para>
///         Like every collection on a record, this compares by REFERENCE under the generated
///         <c>Equals</c>, so two options that differ only by an equal-valued weights array are not
///         <c>==</c>. That matches the other report records in this assembly, which is why no custom
///         equality is declared here.
///     </para>
/// </param>
public sealed record HybridSearchOptions(
    int K = 60,
    int? CandidateDepth = null,
    DistanceFunction? Distance = null,
    HybridTextStyle TextStyle = HybridTextStyle.PlainText,
    string? RegConfig = null,
    IReadOnlyList<double>? ColumnWeights = null)
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

    /// <summary>
    ///     The column weights this search should hand its text leg, checked against the number of
    ///     members the full-text index actually declared. Null means "weigh every column the same".
    /// </summary>
    /// <param name="indexedColumnCount">
    ///     How many members the type's full-text index declared. The store supplies this because only
    ///     the store knows it; every refusal ABOUT it lives here, so a second store honouring weights
    ///     does not invent its own wording (jasperfx#844).
    /// </param>
    /// <remarks>
    ///     <b>A wrong-length array is refused rather than padded or truncated</b>, matching Fisher's
    ///     <c>OrderByRelevance(params double[])</c>. Padding would silently weigh the columns the
    ///     caller forgot at 1.0, and a ranking that is quietly not the one you asked for is the defect
    ///     this option exists to remove, not a tolerance worth having.
    /// </remarks>
    public IReadOnlyList<double>? ResolveColumnWeights(int indexedColumnCount)
    {
        if (ColumnWeights is null)
        {
            return null;
        }

        if (ColumnWeights.Count == 0)
        {
            throw new ArgumentException(
                "ColumnWeights was supplied but empty. Leave it null for the default, which weighs "
                + "every indexed column at 1.0.", nameof(ColumnWeights));
        }

        if (ColumnWeights.Count != indexedColumnCount)
        {
            throw new ArgumentException(
                $"ColumnWeights has {ColumnWeights.Count} weights but the full-text index declares "
                + $"{indexedColumnCount} column(s). Supply one weight per indexed member, in the order "
                + "the index declared them.", nameof(ColumnWeights));
        }

        for (var i = 0; i < ColumnWeights.Count; i++)
        {
            var weight = ColumnWeights[i];
            if (double.IsNaN(weight) || double.IsInfinity(weight))
            {
                throw new ArgumentException(
                    $"ColumnWeights[{i}] is {weight}, which cannot rank anything. Supply a finite "
                    + "weight per indexed column.", nameof(ColumnWeights));
            }
        }

        return ColumnWeights;
    }

    /// <summary>
    ///     Refuse a non-null <see cref="ColumnWeights" /> by name, for a store that has nowhere to put
    ///     per-column weights at query time.
    /// </summary>
    /// <remarks>
    ///     <b>Refusing beats ignoring, and the reason is the whole argument for sharing this type.</b>
    ///     A caller who weights their title column and silently gets an unweighted ranking has no way
    ///     to discover it — the search still returns plausible documents in a plausible order. It is
    ///     the same failure shape as the <see cref="Distance" /> default that made three stores answer
    ///     differently with nothing reported.
    /// </remarks>
    public void AssertColumnWeightsAreNotSupported(string storeName, string alternative)
    {
        if (ColumnWeights is not null)
        {
            throw new NotSupportedException(
                $"{storeName} cannot weight full-text columns at query time, so HybridSearchOptions."
                + $"ColumnWeights has no effect here and is refused rather than ignored. {alternative}");
        }
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
