using System.Security.Cryptography;
using System.Text;

namespace JasperFx.Events.Vectors;

/// <summary>
///     One embedding to write: the document, the text it was built from, that text's hash, and the
///     vector.
/// </summary>
/// <remarks>
///     The hash is stored beside the vector so the NEXT page can skip the model call when the built
///     text has not moved. That is the whole economics of a vector projection over partial-update
///     events: most events change something the embedded text does not mention.
/// </remarks>
public sealed record VectorEmbeddingWrite<TId>(TId Id, string Content, string ContentHash, ReadOnlyMemory<float> Embedding)
    where TId : notnull;

/// <summary>
///     The store-independent body of a vector projection: fold a page of events into the text each
///     document should be embedded from, skip the ones whose text has not changed, and make one
///     batched model call for the rest (jasperfx#841).
/// </summary>
/// <remarks>
///     <para>
///         Everything here is identical for every store, and was written three times: keeping only the
///         last content per id within a page, hashing, comparing against the stored hash, and batching
///         the <see cref="IEmbeddingProvider" /> call. What a store supplies is the two things only it
///         can do — read the current hashes, and write the rows.
///     </para>
///     <para>
///         <b>Not a projection base class.</b> It is a plain object a store's own projection drives,
///         because the base classes differ in ways that are real: Marten writes on its own connection
///         outside the session transaction, Fisher writes through the session, and the id types and
///         registration surfaces are each store's own.
///     </para>
/// </remarks>
public sealed class VectorEmbeddingPlan<TId> where TId : notnull
{
    private readonly Dictionary<TId, string?> _content = [];
    private readonly List<TId> _deletions = [];
    private readonly List<TId> _aggregateIds = [];

    private VectorEmbeddingPlan()
    {
    }

    /// <summary>Documents this page retracts, in the order they were retracted.</summary>
    public IReadOnlyList<TId> Deletions => _deletions;

    /// <summary>
    ///     Documents whose text has to be built from aggregate state, so the store can load them.
    /// </summary>
    /// <remarks>
    ///     Empty unless <see cref="VectorProjectionMap{TId}.MapFromAggregate{TAggregate}" /> was
    ///     declared. Feed each one back through <see cref="ApplyAggregate" />.
    /// </remarks>
    public IReadOnlyList<TId> AggregateIds => _aggregateIds;

    /// <summary>
    ///     Fold a page of events through a map.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Last write per id wins within the page, and a delete wins over everything before it.</b>
    ///         A page is applied as one unit, so embedding an intermediate state would spend a model
    ///         call on text no reader could ever have seen.
    ///     </para>
    ///     <para>
    ///         ⚠️ A delete does NOT suppress content that arrives after it in the same page: a stream
    ///         that is deleted and then written again within one page ends up written, which is the
    ///         state the events describe.
    ///     </para>
    /// </remarks>
    public static VectorEmbeddingPlan<TId> Build(VectorProjectionMap<TId> map, IEnumerable<IEvent> events)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(events);

        var plan = new VectorEmbeddingPlan<TId>();
        var aggregates = new HashSet<TId>();

        foreach (var @event in events)
        {
            if (map.TryDelete(@event, out var deletedId))
            {
                plan._content.Remove(deletedId);
                aggregates.Remove(deletedId);
                plan._deletions.Remove(deletedId);
                plan._deletions.Add(deletedId);
                continue;
            }

            if (map.TryContent(@event, out var id, out var content))
            {
                plan._content[id] = content;
                plan._deletions.Remove(id);
                continue;
            }

            if (map.TryAggregateTrigger(@event, out var aggregateId) && aggregates.Add(aggregateId))
            {
                plan._aggregateIds.Add(aggregateId);
                plan._deletions.Remove(aggregateId);
            }
        }

        return plan;
    }

    /// <summary>
    ///     Supply the aggregate state for one of <see cref="AggregateIds" />, building its content.
    /// </summary>
    /// <param name="id">The document id.</param>
    /// <param name="map">The map that declared the aggregate mapping.</param>
    /// <param name="aggregate">
    ///     The aggregate as it stands after this page's events, or null when the store could not find
    ///     one — which is treated as "nothing to index", the same as a content selector returning null.
    /// </param>
    public void ApplyAggregate(TId id, VectorProjectionMap<TId> map, object? aggregate)
    {
        ArgumentNullException.ThrowIfNull(map);

        _content[id] = aggregate is null ? null : map.ContentFromAggregate(aggregate);
    }

    /// <summary>
    ///     Everything this page would write if no hash matched — id and built text, with the nulls and
    ///     blanks already dropped.
    /// </summary>
    public IReadOnlyDictionary<TId, string> Content => _content
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
        .ToDictionary(pair => pair.Key, pair => pair.Value!);

    /// <summary>
    ///     Resolve the page into the embeddings to write, calling the model once for whatever is left
    ///     after the unchanged documents are dropped.
    /// </summary>
    /// <param name="provider">The embedding model.</param>
    /// <param name="existingHashes">
    ///     The content hash currently stored for each of the given ids, missing entries meaning "no row
    ///     yet". The store's read; nothing else here touches storage.
    /// </param>
    /// <param name="token">Cancellation for the model call and the store's read.</param>
    /// <remarks>
    ///     ⚠️ <b>The model is not called at all when everything is unchanged</b>, which is the point of
    ///     hashing rather than a nicety — <see cref="IEmbeddingProvider" /> implementations charge per
    ///     call and a page of partial-update events usually changes no embedded text whatsoever.
    /// </remarks>
    public async Task<IReadOnlyList<VectorEmbeddingWrite<TId>>> ResolveAsync(
        IEmbeddingProvider provider,
        Func<IReadOnlyList<TId>, CancellationToken, Task<IReadOnlyDictionary<TId, string>>> existingHashes,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(existingHashes);

        var content = Content;
        if (content.Count == 0)
        {
            return [];
        }

        var hashed = content.ToDictionary(pair => pair.Key, pair => (Text: pair.Value, Hash: HashOf(pair.Value)));

        var stored = await existingHashes(hashed.Keys.ToList(), token).ConfigureAwait(false)
                     ?? throw new InvalidOperationException(
                         "The existing-hash lookup returned null. Return an empty dictionary when no row "
                         + "exists yet; null cannot be told apart from a lookup that failed.");

        var changed = hashed
            .Where(pair => !stored.TryGetValue(pair.Key, out var hash) || hash != pair.Value.Hash)
            .ToList();

        if (changed.Count == 0)
        {
            return [];
        }

        var vectors = await provider
            .GenerateEmbeddingsAsync(changed.Select(x => x.Value.Text).ToArray(), token)
            .ConfigureAwait(false);

        if (vectors.Length != changed.Count)
        {
            throw new InvalidOperationException(
                $"{provider.GetType().FullName} returned {vectors.Length} vectors for {changed.Count} texts. "
                + "Vectors are paired with their texts by position, so a differing count would attach "
                + "embeddings to the wrong documents.");
        }

        return changed
            .Select((pair, i) => new VectorEmbeddingWrite<TId>(pair.Key, pair.Value.Text, pair.Value.Hash, vectors[i]))
            .ToList();
    }

    /// <summary>
    ///     The content hash a store stores beside an embedding: SHA-256 of the UTF-8 text, lowercase hex.
    /// </summary>
    /// <remarks>
    ///     Spelled out here rather than left to each store because the hash is PERSISTED: two stores
    ///     hashing the same text differently is invisible until a corpus moves between them, and a
    ///     store that changed its spelling would silently re-embed every document it holds.
    /// </remarks>
    public static string HashOf(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
