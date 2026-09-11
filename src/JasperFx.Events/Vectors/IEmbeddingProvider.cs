namespace JasperFx.Events.Vectors;

/// <summary>
/// A user-supplied embedding model: turns text into fixed-dimension float vectors for similarity
/// search. Implement it with whatever model you run (OpenAI, Ollama, a local ONNX model); the
/// Critter Stack never names a vendor.
/// </summary>
/// <remarks>
/// <para>
/// The store-agnostic replacement for Marten.PgVector's <c>IEmbeddingProvider</c>, lifted here so
/// Marten, Polecat and Fisher share one contract. Marten's original returned
/// <c>Pgvector.Vector[]</c>, which neither of the other two stores can accept: Polecat binds
/// <c>SqlVector&lt;float&gt;</c> and Fisher writes float32 bytes. Both of those, and
/// <c>Pgvector.Vector</c> itself, construct directly from <see cref="ReadOnlyMemory{T}" /> of
/// <see cref="float" />, which is why that is the return element type rather than <c>float[]</c>:
/// a provider may hand out slices of one pooled buffer, and no store has to copy on the way in.
/// See <see href="https://github.com/JasperFx/jasperfx/issues/811" />.
/// </para>
/// <para>
/// <strong>Batch by design.</strong> The one method takes an array because embedding models charge
/// per call, not per text, and every consumer that matters (a projection folding a batch of events,
/// a session listener with a unit of work) has several texts in hand. A single-text convenience is
/// <see cref="EmbeddingProviderExtensions.GenerateEmbeddingAsync" />; it is an extension so an
/// implementer has exactly one method to write.
/// </para>
/// <para>
/// <strong>Where this is called from matters more than what it returns.</strong> A model call is a
/// network round trip. Called inline from a session's pre-commit hook it holds the store's write
/// transaction open for that round trip, which on a single-writer file store such as Fisher blocks
/// every other writer for the duration. The stores' vector projections run it from the async daemon
/// for that reason, and an inline path is for tests and tiny workloads only.
/// </para>
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>
    /// The number of elements in every vector this provider produces.
    /// </summary>
    /// <remarks>
    /// Stores read this once to size the column (<c>vector(768)</c>, <c>VECTOR(768)</c>, a
    /// 768 × 4 byte BLOB). A provider must return vectors of exactly this length; a store may
    /// validate and throw rather than write a truncated or padded row.
    /// </remarks>
    int Dimensions { get; }

    /// <summary>
    /// Embed one or more texts.
    /// </summary>
    /// <param name="texts">The texts to embed, in the order the results must come back.</param>
    /// <param name="ct">Cancellation for the model call.</param>
    /// <returns>
    /// One vector per input, in input order, each of length <see cref="Dimensions" />. The
    /// returned array must have the same length as <paramref name="texts" />; a store pairs them
    /// by position and has no other way to match a vector to its text.
    /// </returns>
    /// <remarks>
    /// An empty <paramref name="texts" /> returns an empty array and must not call the model.
    /// Stores rely on that so a batch in which every text was skipped by content-hash comparison
    /// costs nothing.
    /// </remarks>
    Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default);
}

/// <summary>
/// Conveniences over <see cref="IEmbeddingProvider" /> so implementers write only the batch method.
/// </summary>
public static class EmbeddingProviderExtensions
{
    /// <summary>
    /// Embed a single text.
    /// </summary>
    /// <remarks>
    /// The query-side call: a search takes one string from a user or an agent and needs one vector
    /// to compare against. Delegates to <see cref="IEmbeddingProvider.GenerateEmbeddingsAsync" />
    /// with a one-element batch.
    /// </remarks>
    public static async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        this IEmbeddingProvider provider,
        string text,
        CancellationToken ct = default)
    {
        var vectors = await provider.GenerateEmbeddingsAsync([text], ct).ConfigureAwait(false);
        if (vectors.Length != 1)
        {
            throw new InvalidOperationException(
                $"{provider.GetType().FullName} returned {vectors.Length} vectors for a single text; " +
                $"{nameof(IEmbeddingProvider.GenerateEmbeddingsAsync)} must return exactly one vector per input.");
        }

        return vectors[0];
    }
}
