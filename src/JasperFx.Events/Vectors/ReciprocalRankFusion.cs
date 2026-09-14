namespace JasperFx.Events.Vectors;

/// <summary>
///     Reciprocal rank fusion, as a pure function over ranked lists (jasperfx#844).
/// </summary>
/// <remarks>
///     <para>
///         <c>score(d) = Σ 1 / (k + rank(d))</c> over the legs that found it, ranks 1-based.
///     </para>
///     <para>
///         ⚠️ <b>The fusion reads ORDINAL POSITION, never the legs' own scores.</b> A bm25 or
///         <c>ts_rank</c> relevance and a cosine distance are not on a comparable scale and do not even
///         run in the same direction, so normalising them into one number means picking constants that
///         are wrong for somebody's corpus. RRF needs only each leg's ordering, so the two need no
///         calibration and the behaviour does not move when the embedding model or the tokenizer
///         changes.
///     </para>
///     <para>
///         <b>Fused BY KEY, so the legs need not be the same document type.</b> That is the case the
///         stores' own hybrid search cannot serve: a snapshot document carries the full-text index while
///         a separate embedding document carries the vector, which is exactly the shape a vector
///         projection writes. Each store previously had a private copy of this that could only fuse one
///         type with itself, and a consumer with the split shape had to write a fourth.
///     </para>
///     <para>
///         <b>Ties break deterministically, and that is not tidiness.</b> Two documents found at the
///         same rank by one leg and by neither in the other have identical scores, which is common
///         rather than exotic. Without a total order the page a caller gets differs between runs.
///     </para>
/// </remarks>
public static class ReciprocalRankFusion
{
    /// <summary>The smoothing constant the literature uses, and every store's default.</summary>
    public const int DefaultK = 60;

    /// <summary>
    ///     Fuse any number of ranked legs into one ranking.
    /// </summary>
    /// <param name="legs">
    ///     Each leg in its own rank order, best first. A document appearing in more than one leg scores
    ///     the sum, which is what makes agreement between legs outrank a single-leg winner.
    /// </param>
    /// <param name="key">The identity a document is fused on — legs may be different types.</param>
    /// <param name="limit">How many to return.</param>
    /// <param name="k">The smoothing constant; see <see cref="DefaultK" />.</param>
    /// <remarks>
    ///     The instance kept for a key is the FIRST leg's, deliberately: legs are expected to
    ///     materialise the same row, so keeping one makes reference identity within a result stable.
    /// </remarks>
    public static IReadOnlyList<HybridMatch<T>> Fuse<T, TKey>(
        IEnumerable<IReadOnlyList<T>> legs,
        Func<T, TKey> key,
        int limit,
        int k = DefaultK) where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(key);

        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1");
        }

        if (k < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k,
                "k must be at least 1. It is the smoothing constant, and 0 would make the top-ranked "
                + "document of any leg score infinitely.");
        }

        var fused = new Dictionary<TKey, Candidate<T, TKey>>();

        foreach (var leg in legs)
        {
            if (leg is null) continue;

            for (var i = 0; i < leg.Count; i++)
            {
                var document = leg[i];
                var id = key(document);
                var rank = i + 1;

                if (fused.TryGetValue(id, out var existing))
                {
                    existing.Score += 1.0 / (k + rank);
                    existing.BestRank = Math.Min(existing.BestRank, rank);
                }
                else
                {
                    fused[id] = new Candidate<T, TKey>(document, 1.0 / (k + rank), rank, id);
                }
            }
        }

        return fused.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.BestRank)
            .ThenBy(x => x.Key.ToString(), StringComparer.Ordinal)
            .Take(limit)
            .Select(x => new HybridMatch<T>(x.Document, x.Score))
            .ToList();
    }

    /// <summary>Fuse exactly two legs — the shape every store's hybrid search uses.</summary>
    public static IReadOnlyList<HybridMatch<T>> Fuse<T, TKey>(
        IReadOnlyList<T> textLeg,
        IReadOnlyList<T> vectorLeg,
        Func<T, TKey> key,
        int limit,
        int k = DefaultK) where TKey : notnull
        => Fuse([textLeg, vectorLeg], key, limit, k);

    private sealed class Candidate<T, TKey>(T document, double score, int bestRank, TKey id)
        where TKey : notnull
    {
        public T Document { get; } = document;
        public double Score { get; set; } = score;
        public int BestRank { get; set; } = bestRank;
        public TKey Key { get; } = id;
    }
}
