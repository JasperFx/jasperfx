using JasperFx.Events.Vectors;
using Microsoft.Extensions.AI;

namespace JasperFx.Events.MicrosoftExtensionsAI;

/// <summary>
/// An <see cref="IEmbeddingProvider" /> over a Microsoft.Extensions.AI
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}" />, so a model already configured for
/// OpenAI, Azure OpenAI, Ollama or any other M.E.AI provider drives vector search in Marten, Polecat
/// and Fisher without a hand-written provider.
/// </summary>
/// <remarks>
/// <para>
/// The adapter is deliberately thin: it forwards a batch, then enforces the two rules
/// <see cref="IEmbeddingProvider" /> makes to the stores before a store sees the result — one
/// vector per input, in input order, and every vector of length <see cref="Dimensions" />. A
/// generator that violates either throws here with the model named, rather than a store writing a
/// truncated row or pairing a vector with the wrong document. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/813" />.
/// </para>
/// <para>
/// <strong>Dimensions must be known before the first call.</strong> Stores size their column from
/// <see cref="Dimensions" /> at schema time, before any text has been embedded, so the value cannot
/// be discovered lazily from the first result. Construct with an explicit count, or through
/// <see cref="EmbeddingGeneratorExtensions.AsEmbeddingProvider" />, which falls back to the
/// generator's own <see cref="EmbeddingGeneratorMetadata.DefaultModelDimensions" /> when the
/// provider publishes one.
/// </para>
/// <para>
/// <see cref="EmbeddingGenerationOptions" /> are passed through verbatim and nothing is synthesised
/// from <see cref="Dimensions" />: some models accept a requested dimension count and some reject
/// it, and the person who configured the generator is the one who knows which. If the model should
/// be asked for a specific size, say so in the options you supply.
/// </para>
/// </remarks>
public sealed class EmbeddingGeneratorProvider : IEmbeddingProvider
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly EmbeddingGenerationOptions? _options;

    /// <summary>
    /// Wrap a generator whose vector length is <paramref name="dimensions" />.
    /// </summary>
    /// <param name="generator">The Microsoft.Extensions.AI generator to forward to.</param>
    /// <param name="dimensions">
    /// The length of every vector the generator returns. Validated against each result.
    /// </param>
    /// <param name="options">
    /// Options forwarded on every call, or <c>null</c> for the generator's defaults.
    /// </param>
    public EmbeddingGeneratorProvider(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        int dimensions,
        EmbeddingGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);

        _generator = generator;
        _options = options;
        Dimensions = dimensions;
    }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Length == 0)
        {
            return [];
        }

        var generated = await _generator.GenerateAsync(texts, _options, ct).ConfigureAwait(false);

        if (generated.Count != texts.Length)
        {
            throw new InvalidOperationException(
                $"{describe()} returned {generated.Count} embeddings for {texts.Length} texts. " +
                $"{nameof(IEmbeddingProvider)} requires exactly one vector per input, in input order.");
        }

        var vectors = new ReadOnlyMemory<float>[generated.Count];
        for (var i = 0; i < vectors.Length; i++)
        {
            var vector = generated[i].Vector;
            if (vector.Length != Dimensions)
            {
                throw new InvalidOperationException(
                    $"{describe()} returned a vector of length {vector.Length} for text {i}, but this provider " +
                    $"was declared with {nameof(Dimensions)} = {Dimensions}. Stores size their vector column from " +
                    $"{nameof(Dimensions)}, so the two must agree; fix the declared count or the model configuration.");
            }

            vectors[i] = vector;
        }

        return vectors;
    }

    private string describe()
    {
        var metadata = _generator.GetService<EmbeddingGeneratorMetadata>();
        var model = metadata?.DefaultModelId;
        var name = metadata?.ProviderName ?? _generator.GetType().Name;
        return model is null ? name : $"{name} ({model})";
    }
}

/// <summary>
/// The one-line way to hand a Microsoft.Extensions.AI generator to a Critter Stack store.
/// </summary>
public static class EmbeddingGeneratorExtensions
{
    /// <summary>
    /// Adapt this generator to <see cref="IEmbeddingProvider" />.
    /// </summary>
    /// <param name="generator">The generator to adapt.</param>
    /// <param name="dimensions">
    /// The vector length, or <c>null</c> to take it from the generator's
    /// <see cref="EmbeddingGeneratorMetadata.DefaultModelDimensions" />. Supplying it explicitly is
    /// always allowed and wins over the metadata.
    /// </param>
    /// <param name="options">Options forwarded on every call, or <c>null</c> for the defaults.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="dimensions" /> is <c>null</c> and the generator publishes no default
    /// dimension count. The store needs the number at schema time, so it has to come from somewhere.
    /// </exception>
    public static IEmbeddingProvider AsEmbeddingProvider(
        this IEmbeddingGenerator<string, Embedding<float>> generator,
        int? dimensions = null,
        EmbeddingGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(generator);

        var resolved = dimensions
                       ?? options?.Dimensions
                       ?? generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelDimensions
                       ?? throw new InvalidOperationException(
                           $"{generator.GetType().Name} does not publish a default dimension count in its " +
                           $"{nameof(EmbeddingGeneratorMetadata)}, so pass dimensions explicitly: " +
                           $"generator.{nameof(AsEmbeddingProvider)}(dimensions: 1536). Stores size their vector " +
                           "column from it before the first text is embedded.");

        return new EmbeddingGeneratorProvider(generator, resolved, options);
    }
}
