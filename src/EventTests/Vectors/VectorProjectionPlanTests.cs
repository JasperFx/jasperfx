using JasperFx.Events;
using JasperFx.Events.Vectors;
using Shouldly;

namespace EventTests.Vectors;

public record MemoryRecorded(Guid Id, string Title, string Body);

public record MemoryRevised(Guid Id, string? Title, string? Body);

public record MemoryForgotten(Guid Id);

public class MemorySnapshot
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

// The shared vector projection core (jasperfx#841): the map, the per-page fold, content hashing and
// the batched model call, none of which any store should be writing for itself any more.
public class VectorProjectionMapTests
{
    private static IEvent Wrap<T>(T body, Guid stream) where T : notnull
        => new Event<T>(body) { StreamId = stream, Id = Guid.NewGuid() };

    [Fact]
    public void maps_content_from_the_event_wrapper_not_the_bare_body()
    {
        var id = Guid.NewGuid();
        var map = new VectorProjectionMap<Guid>()
            // The wrapper can always reach the body; the body can never reach the wrapper. Marten's
            // map handed over the body only, so metadata could not contribute at all.
            .Map<MemoryRecorded>(e => $"{e.Data.Title} {e.StreamId}", e => e.Data.Id);

        map.TryContent(Wrap(new MemoryRecorded(id, "hello", "body"), id), out var mapped, out var content)
            .ShouldBeTrue();

        mapped.ShouldBe(id);
        content.ShouldBe($"hello {id}");
    }

    [Fact]
    public void refuses_two_content_mappings_for_one_event_type()
    {
        var map = new VectorProjectionMap<Guid>().Map<MemoryRecorded>(e => e.Data.Title, e => e.Data.Id);

        Should.Throw<InvalidOperationException>(() =>
            map.Map<MemoryRecorded>(e => e.Data.Body, e => e.Data.Id));
    }

    [Fact]
    public void refuses_one_event_type_mapped_for_both_content_and_deletion()
    {
        var map = new VectorProjectionMap<Guid>().Map<MemoryRecorded>(e => e.Data.Title, e => e.Data.Id);

        Should.Throw<InvalidOperationException>(() => map.Delete<MemoryRecorded>(e => e.Data.Id));

        var other = new VectorProjectionMap<Guid>().Delete<MemoryForgotten>(e => e.Data.Id);
        Should.Throw<InvalidOperationException>(() =>
            other.Map<MemoryForgotten>(e => "x", e => e.Data.Id));
    }

    [Fact]
    public void a_selector_that_throws_is_not_swallowed()
    {
        // ⚠️ Marten's template caught everything and returned null, which the caller read as "no
        // content" — so a buggy selector dropped the document from the index with nothing reported.
        var map = new VectorProjectionMap<Guid>()
            .Map<MemoryRecorded>(_ => throw new InvalidOperationException("boom"), e => e.Data.Id);

        Should.Throw<InvalidOperationException>(() =>
            map.TryContent(Wrap(new MemoryRecorded(Guid.NewGuid(), "t", "b"), Guid.NewGuid()), out _, out _));
    }

    [Fact]
    public void an_aggregate_mapping_needs_at_least_one_trigger()
    {
        Should.Throw<ArgumentException>(() =>
            new VectorProjectionMap<Guid>().MapFromAggregate<MemorySnapshot>(x => x.Title));
    }

    [Fact]
    public void refuses_a_second_aggregate_mapping()
    {
        var map = new VectorProjectionMap<Guid>()
            .MapFromAggregate<MemorySnapshot>(x => x.Title, (typeof(MemoryRecorded), e => e.StreamId));

        Should.Throw<InvalidOperationException>(() =>
            map.MapFromAggregate<MemorySnapshot>(x => x.Body, (typeof(MemoryRevised), e => e.StreamId)));
    }

    [Fact]
    public void reports_the_event_types_a_store_has_to_subscribe_to()
    {
        var map = new VectorProjectionMap<Guid>()
            .Map<MemoryRecorded>(e => e.Data.Title, e => e.Data.Id)
            .Delete<MemoryForgotten>(e => e.Data.Id);

        map.ContentEventTypes.ShouldBe([typeof(MemoryRecorded)]);
        map.DeleteEventTypes.ShouldBe([typeof(MemoryForgotten)]);
        map.AggregateType.ShouldBeNull();
        map.IsEmpty.ShouldBeFalse();
        new VectorProjectionMap<Guid>().IsEmpty.ShouldBeTrue();
    }
}

public class VectorEmbeddingPlanTests
{
    private static readonly Guid Stream = Guid.NewGuid();

    private static IEvent Wrap<T>(T body) where T : notnull
        => new Event<T>(body) { StreamId = Stream, Id = Guid.NewGuid() };

    private static VectorProjectionMap<Guid> EventMap() => new VectorProjectionMap<Guid>()
        .Map<MemoryRecorded>(e => $"{e.Data.Title} {e.Data.Body}", e => e.Data.Id)
        .Delete<MemoryForgotten>(e => e.Data.Id);

    [Fact]
    public void keeps_only_the_last_content_per_id_within_a_page()
    {
        // A page is applied as one unit, so embedding an intermediate state would spend a model call
        // on text no reader could ever have seen.
        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), [
            Wrap(new MemoryRecorded(Stream, "first", "body")),
            Wrap(new MemoryRecorded(Stream, "second", "body"))
        ]);

        plan.Content.Count.ShouldBe(1);
        plan.Content[Stream].ShouldBe("second body");
    }

    [Fact]
    public void a_delete_drops_content_recorded_earlier_in_the_page()
    {
        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), [
            Wrap(new MemoryRecorded(Stream, "first", "body")),
            Wrap(new MemoryForgotten(Stream))
        ]);

        plan.Content.ShouldBeEmpty();
        plan.Deletions.ShouldBe([Stream]);
    }

    [Fact]
    public void content_after_a_delete_in_the_same_page_wins()
    {
        // The state the events describe: deleted, then written again.
        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), [
            Wrap(new MemoryForgotten(Stream)),
            Wrap(new MemoryRecorded(Stream, "again", "body"))
        ]);

        plan.Deletions.ShouldBeEmpty();
        plan.Content[Stream].ShouldBe("again body");
    }

    [Fact]
    public void null_and_blank_content_index_nothing()
    {
        var map = new VectorProjectionMap<Guid>().Map<MemoryRevised>(e => e.Data.Body, e => e.Data.Id);

        var plan = VectorEmbeddingPlan<Guid>.Build(map, [
            Wrap(new MemoryRevised(Stream, null, null)),
            Wrap(new MemoryRevised(Guid.NewGuid(), null, "   "))
        ]);

        plan.Content.ShouldBeEmpty();
    }

    [Fact]
    public async Task calls_the_model_once_for_the_whole_page()
    {
        var provider = new CountingProvider(3);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), [
            Wrap(new MemoryRecorded(a, "alpha", "one")),
            Wrap(new MemoryRecorded(b, "beta", "two"))
        ]);

        var writes = await plan.ResolveAsync(provider, (_, _) => NoHashes(), TestContext.Current.CancellationToken);

        writes.Count.ShouldBe(2);
        provider.Calls.ShouldBe(1);
        writes.Select(x => x.Content).OrderBy(x => x).ShouldBe(["alpha one", "beta two"]);
        writes.ShouldAllBe(x => x.Embedding.Length == 3);
    }

    [Fact]
    public async Task unchanged_content_costs_no_model_call_at_all()
    {
        // The whole economics of a vector projection over partial-update events. This is also the
        // reason the hash is persisted rather than recomputed from the stored vector.
        var provider = new CountingProvider(3);
        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), [Wrap(new MemoryRecorded(Stream, "alpha", "one"))]);

        var stored = new Dictionary<Guid, string> { [Stream] = VectorEmbeddingPlan<Guid>.HashOf("alpha one") };

        var writes = await plan.ResolveAsync(provider, (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(stored),
            TestContext.Current.CancellationToken);

        writes.ShouldBeEmpty();
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task only_the_changed_documents_reach_the_model()
    {
        var provider = new CountingProvider(3);
        var unchanged = Guid.NewGuid();
        var changed = Guid.NewGuid();

        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), [
            Wrap(new MemoryRecorded(unchanged, "same", "text")),
            Wrap(new MemoryRecorded(changed, "new", "text"))
        ]);

        var stored = new Dictionary<Guid, string>
        {
            [unchanged] = VectorEmbeddingPlan<Guid>.HashOf("same text"),
            [changed] = VectorEmbeddingPlan<Guid>.HashOf("something else")
        };

        var writes = await plan.ResolveAsync(provider, (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(stored),
            TestContext.Current.CancellationToken);

        writes.Single().Id.ShouldBe(changed);
        provider.Texts.Single().ShouldBe("new text");
    }

    [Fact]
    public async Task refuses_a_provider_that_returns_a_differing_number_of_vectors()
    {
        // Vectors are paired with their texts BY POSITION, so a differing count would attach
        // embeddings to the wrong documents rather than fail.
        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), [
            Wrap(new MemoryRecorded(Guid.NewGuid(), "a", "1")),
            Wrap(new MemoryRecorded(Guid.NewGuid(), "b", "2"))
        ]);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            plan.ResolveAsync(new ShortProvider(3), (_, _) => NoHashes(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task an_aggregate_mapping_carries_state_the_partial_update_event_does_not()
    {
        // The failure this whole feature exists for: MemoryRevised carries a new body and nulls for
        // everything else. An event-only selector had to choose between re-embedding without the
        // title (wrong) and skipping (stale). Neither is an option here.
        var map = new VectorProjectionMap<Guid>()
            .MapFromAggregate<MemorySnapshot>(
                x => $"{x.Title} {x.Body}",
                (typeof(MemoryRecorded), e => e.StreamId),
                (typeof(MemoryRevised), e => e.StreamId));

        var plan = VectorEmbeddingPlan<Guid>.Build(map, [
            Wrap(new MemoryRecorded(Stream, "Kept Title", "old body")),
            Wrap(new MemoryRevised(Stream, null, "new body"))
        ]);

        // One load per affected stream per page, not per event.
        plan.AggregateIds.ShouldBe([Stream]);

        plan.ApplyAggregate(Stream, map, new MemorySnapshot { Id = Stream, Title = "Kept Title", Body = "new body" });

        plan.Content[Stream].ShouldBe("Kept Title new body");

        var writes = await plan.ResolveAsync(new CountingProvider(3), (_, _) => NoHashes(),
            TestContext.Current.CancellationToken);

        writes.Single().Content.ShouldBe("Kept Title new body");
    }

    [Fact]
    public void a_missing_aggregate_indexes_nothing_rather_than_throwing()
    {
        var map = new VectorProjectionMap<Guid>()
            .MapFromAggregate<MemorySnapshot>(x => x.Title, (typeof(MemoryRecorded), e => e.StreamId));

        var plan = VectorEmbeddingPlan<Guid>.Build(map, [Wrap(new MemoryRecorded(Stream, "t", "b"))]);
        plan.ApplyAggregate(Stream, map, null);

        plan.Content.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_empty_page_never_reaches_the_store_or_the_model()
    {
        var provider = new CountingProvider(3);
        var plan = VectorEmbeddingPlan<Guid>.Build(EventMap(), []);

        var writes = await plan.ResolveAsync(provider,
            (_, _) => throw new ShouldAssertException("the existing-hash lookup should not have been called"),
            TestContext.Current.CancellationToken);

        writes.ShouldBeEmpty();
        provider.Calls.ShouldBe(0);
    }

    [Fact]
    public void the_content_hash_is_sha256_hex_of_the_utf8_text()
    {
        // Pinned because it is PERSISTED: two stores hashing differently is invisible until a corpus
        // moves between them, and a changed spelling silently re-embeds every document a store holds.
        VectorEmbeddingPlan<Guid>.HashOf("abc")
            .ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    private static Task<IReadOnlyDictionary<Guid, string>> NoHashes()
        => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    private sealed class CountingProvider(int dimensions) : IEmbeddingProvider
    {
        public int Dimensions { get; } = dimensions;
        public int Calls { get; private set; }
        public List<string> Texts { get; } = [];

        public Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default)
        {
            Calls++;
            Texts.AddRange(texts);
            return Task.FromResult(texts
                .Select(t => new ReadOnlyMemory<float>(Enumerable.Repeat((float)t.Length, Dimensions).ToArray()))
                .ToArray());
        }
    }

    private sealed class ShortProvider(int dimensions) : IEmbeddingProvider
    {
        public int Dimensions { get; } = dimensions;

        public Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default)
            => Task.FromResult<ReadOnlyMemory<float>[]>([new ReadOnlyMemory<float>(new float[Dimensions])]);
    }
}
