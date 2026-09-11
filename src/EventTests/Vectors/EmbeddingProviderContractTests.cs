using JasperFx.Events.Vectors;
using Shouldly;

namespace EventTests.Vectors;

// Compile-pins the store-neutral vector contracts (jasperfx#811) by implementing them, and checks
// the single-text convenience and the "one vector per input" rule it enforces.
public class EmbeddingProviderContractTests
{
    [Fact]
    public async Task implementable_and_batch_preserves_order_and_dimensions()
    {
        IEmbeddingProvider provider = new FakeProvider(dimensions: 3);

        var vectors = await provider.GenerateEmbeddingsAsync(["alpha", "beta"], TestContext.Current.CancellationToken);

        vectors.Length.ShouldBe(2);
        vectors[0].Length.ShouldBe(3);
        vectors[1].Length.ShouldBe(3);
        vectors[0].Span[0].ShouldBe("alpha".Length);
        vectors[1].Span[0].ShouldBe("beta".Length);
    }

    [Fact]
    public async Task empty_input_returns_empty_without_calling_the_model()
    {
        var provider = new FakeProvider(dimensions: 3);

        var vectors = await provider.GenerateEmbeddingsAsync([], TestContext.Current.CancellationToken);

        vectors.ShouldBeEmpty();
        provider.ModelCalls.ShouldBe(0);
    }

    [Fact]
    public async Task single_text_convenience_delegates_to_the_batch_method()
    {
        var provider = new FakeProvider(dimensions: 3);

        var vector = await provider.GenerateEmbeddingAsync("gamma", TestContext.Current.CancellationToken);

        vector.Length.ShouldBe(3);
        vector.Span[0].ShouldBe("gamma".Length);
        provider.ModelCalls.ShouldBe(1);
    }

    [Fact]
    public async Task single_text_convenience_refuses_a_provider_that_breaks_the_one_per_input_rule()
    {
        IEmbeddingProvider broken = new BrokenProvider();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => broken.GenerateEmbeddingAsync("x"));

        ex.Message.ShouldContain("returned 2 vectors for a single text");
    }

    [Fact]
    public void distance_function_keeps_marten_pgvector_member_names()
    {
        // Marten aliases its own enum onto this one by name; a rename here breaks user code there.
        Enum.GetNames<DistanceFunction>().ShouldBe(["Cosine", "L2", "InnerProduct"]);
        default(DistanceFunction).ShouldBe(DistanceFunction.Cosine);
    }

    [Fact]
    public void vector_match_carries_document_and_double_distance()
    {
        var match = new VectorMatch<string>("doc", 0.25);

        match.Document.ShouldBe("doc");
        match.Distance.ShouldBe(0.25);
        match.ShouldBe(new VectorMatch<string>("doc", 0.25));
    }

    // Deterministic stand-in: first element is the text length, the rest zero.
    private sealed class FakeProvider(int dimensions) : IEmbeddingProvider
    {
        public int ModelCalls { get; private set; }

        public int Dimensions { get; } = dimensions;

        public Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default)
        {
            if (texts.Length == 0)
            {
                return Task.FromResult(Array.Empty<ReadOnlyMemory<float>>());
            }

            ModelCalls++;
            var result = new ReadOnlyMemory<float>[texts.Length];
            for (var i = 0; i < texts.Length; i++)
            {
                var floats = new float[Dimensions];
                floats[0] = texts[i].Length;
                result[i] = floats;
            }

            return Task.FromResult(result);
        }
    }

    private sealed class BrokenProvider : IEmbeddingProvider
    {
        public int Dimensions => 1;

        public Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default)
            => Task.FromResult(new ReadOnlyMemory<float>[] { new float[] { 1f }, new float[] { 2f } });
    }
}
