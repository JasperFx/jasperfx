using JasperFx.Events.MicrosoftExtensionsAI;
using JasperFx.Events.Vectors;
using Microsoft.Extensions.AI;
using Shouldly;

namespace JasperFx.Events.MicrosoftExtensionsAI.Tests;

public class EmbeddingGeneratorProviderTests
{
    [Fact]
    public async Task forwards_a_batch_in_order_and_hands_back_the_vectors()
    {
        var generator = new FakeGenerator(dimensions: 3);
        IEmbeddingProvider provider = generator.AsEmbeddingProvider(dimensions: 3);

        var vectors = await provider.GenerateEmbeddingsAsync(["alpha", "be"], TestContext.Current.CancellationToken);

        vectors.Length.ShouldBe(2);
        vectors[0].Span[0].ShouldBe(5f);
        vectors[1].Span[0].ShouldBe(2f);
        generator.Calls.ShouldBe(1);
        generator.LastInputs.ShouldBe(["alpha", "be"]);
    }

    [Fact]
    public async Task empty_input_returns_empty_without_calling_the_generator()
    {
        var generator = new FakeGenerator(dimensions: 3);
        var provider = generator.AsEmbeddingProvider(dimensions: 3);

        var vectors = await provider.GenerateEmbeddingsAsync([], TestContext.Current.CancellationToken);

        vectors.ShouldBeEmpty();
        generator.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task options_are_forwarded_verbatim()
    {
        var generator = new FakeGenerator(dimensions: 3);
        var options = new EmbeddingGenerationOptions { ModelId = "custom" };
        var provider = generator.AsEmbeddingProvider(dimensions: 3, options: options);

        await provider.GenerateEmbeddingsAsync(["x"], TestContext.Current.CancellationToken);

        generator.LastOptions.ShouldBeSameAs(options);
    }

    [Fact]
    public void dimensions_come_from_metadata_when_not_supplied()
    {
        var generator = new FakeGenerator(dimensions: 3, defaultModelDimensions: 768);

        var provider = generator.AsEmbeddingProvider();

        provider.Dimensions.ShouldBe(768);
    }

    [Fact]
    public void explicit_dimensions_win_over_metadata()
    {
        var generator = new FakeGenerator(dimensions: 3, defaultModelDimensions: 768);

        generator.AsEmbeddingProvider(dimensions: 3).Dimensions.ShouldBe(3);
    }

    [Fact]
    public void requested_dimensions_in_options_are_used_when_nothing_else_says()
    {
        var generator = new FakeGenerator(dimensions: 3);

        var provider = generator.AsEmbeddingProvider(options: new EmbeddingGenerationOptions { Dimensions = 3 });

        provider.Dimensions.ShouldBe(3);
    }

    [Fact]
    public void refuses_to_construct_when_dimensions_are_unknowable()
    {
        var generator = new FakeGenerator(dimensions: 3);

        var ex = Should.Throw<InvalidOperationException>(() => generator.AsEmbeddingProvider());

        ex.Message.ShouldContain("pass dimensions explicitly");
    }

    [Fact]
    public async Task refuses_a_generator_that_returns_the_wrong_count()
    {
        var generator = new FakeGenerator(dimensions: 3) { ExtraEmbeddings = 1 };
        var provider = generator.AsEmbeddingProvider(dimensions: 3);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => provider.GenerateEmbeddingsAsync(["a"]));

        ex.Message.ShouldContain("returned 2 embeddings for 1 texts");
    }

    [Fact]
    public async Task refuses_a_vector_of_the_wrong_length()
    {
        var generator = new FakeGenerator(dimensions: 4);
        var provider = generator.AsEmbeddingProvider(dimensions: 3);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => provider.GenerateEmbeddingsAsync(["a"]));

        ex.Message.ShouldContain("length 4 for text 0");
        ex.Message.ShouldContain("Dimensions = 3");
    }

    [Fact]
    public void constructor_validates_its_arguments()
    {
        Should.Throw<ArgumentNullException>(() => new EmbeddingGeneratorProvider(null!, 3));
        Should.Throw<ArgumentOutOfRangeException>(() => new EmbeddingGeneratorProvider(new FakeGenerator(3), 0));
    }

    // First element of each vector is the text length; the rest are zero.
    private sealed class FakeGenerator(int dimensions, int? defaultModelDimensions = null)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly EmbeddingGeneratorMetadata _metadata =
            new("fake", defaultModelId: "fake-model", defaultModelDimensions: defaultModelDimensions);

        public int Calls { get; private set; }
        public string[] LastInputs { get; private set; } = [];
        public EmbeddingGenerationOptions? LastOptions { get; private set; }
        public int ExtraEmbeddings { get; init; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastInputs = values.ToArray();
            LastOptions = options;

            var embeddings = LastInputs
                .Select(text =>
                {
                    var floats = new float[dimensions];
                    floats[0] = text.Length;
                    return new Embedding<float>(floats);
                })
                .ToList();

            for (var i = 0; i < ExtraEmbeddings; i++)
            {
                embeddings.Add(new Embedding<float>(new float[dimensions]));
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : null;

        public void Dispose()
        {
        }
    }
}
