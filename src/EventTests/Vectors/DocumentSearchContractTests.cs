using System.Linq.Expressions;
using JasperFx.Events.Documents;
using JasperFx.Events.Vectors;
using Shouldly;

namespace EventTests.Vectors;

// Compile-pins the store-neutral search contract (jasperfx#842, with #843's filter in the signature
// from the start) by implementing it, and checks the document-only conveniences built over it.
public class DocumentSearchContractTests
{
    public sealed record Memory(string Id, string Scope);

    [Fact]
    public async Task the_document_only_vector_search_is_the_scored_one_projected()
    {
        var search = new RecordingSearch();

        var documents = await search.VectorSearchAsync<Memory>(
            x => x.Id, new float[] { 1, 2, 3 }, limit: 2, token: TestContext.Current.CancellationToken);

        documents.Select(x => x.Id).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task the_document_only_hybrid_search_is_the_scored_one_projected()
    {
        var search = new RecordingSearch();

        var documents = await search.HybridSearchAsync<Memory>(
            x => x.Id, "text", new float[] { 1, 2, 3 }, limit: 2, token: TestContext.Current.CancellationToken);

        documents.Select(x => x.Id).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task the_filter_reaches_the_store_rather_than_being_applied_afterwards()
    {
        // jasperfx#843: filtering after the fact can return nothing at all when the globally nearest
        // rows belong to other scopes, so the predicate has to be the store's to push down.
        var search = new RecordingSearch();

        await search.VectorSearchAsync<Memory>(x => x.Id, new float[] { 1 },
            filter: x => x.Scope == "project-a", token: TestContext.Current.CancellationToken);

        search.LastFilter.ShouldNotBeNull();
        search.LastFilter.ToString().ShouldContain("project-a");
    }

    [Fact]
    public async Task the_hybrid_filter_reaches_the_store_too()
    {
        var search = new RecordingSearch();

        await search.HybridSearchAsync<Memory>(x => x.Id, "text", new float[] { 1 },
            filter: x => x.Scope == "project-a", token: TestContext.Current.CancellationToken);

        search.LastFilter!.ToString().ShouldContain("project-a");
    }

    [Fact]
    public void a_store_that_has_not_implemented_search_says_so_by_name()
    {
        // The default is the same rule IDocumentReadOperations.Events follows: a store picks up a new
        // JasperFx without a compile break, and what holds it to the behavior is the compliance suite.
        IDocumentReadOperations session = new SearchlessSession();

        var ex = Should.Throw<NotSupportedException>(() => _ = session.Search);

        ex.Message.ShouldContain(typeof(SearchlessSession).FullName!);
        ex.Message.ShouldContain(nameof(IDocumentReadOperations.Search));
    }

    [Fact]
    public void a_store_that_has_implemented_search_returns_it()
    {
        IDocumentReadOperations session = new SearchingSession();

        session.Search.ShouldBeOfType<RecordingSearch>();
    }

    private sealed class RecordingSearch : IDocumentSearchOperations
    {
        public object? LastFilter { get; private set; }

        public Task<IReadOnlyList<VectorMatch<T>>> VectorSearchWithScoresAsync<T>(
            Expression<Func<T, object?>> member, ReadOnlyMemory<float> query, int limit = 10,
            DistanceFunction? distance = null, Expression<Func<T, bool>>? filter = null,
            CancellationToken token = default) where T : notnull
        {
            LastFilter = filter;
            return Task.FromResult<IReadOnlyList<VectorMatch<T>>>([
                new VectorMatch<T>((T)(object)new Memory("a", "s"), 0.1),
                new VectorMatch<T>((T)(object)new Memory("b", "s"), 0.2)
            ]);
        }

        public Task<IReadOnlyList<HybridMatch<T>>> HybridSearchWithScoresAsync<T>(
            Expression<Func<T, object?>> vectorMember, string text, ReadOnlyMemory<float> query, int limit = 10,
            HybridSearchOptions? options = null, Expression<Func<T, bool>>? filter = null,
            CancellationToken token = default) where T : notnull
        {
            LastFilter = filter;
            return Task.FromResult<IReadOnlyList<HybridMatch<T>>>([
                new HybridMatch<T>((T)(object)new Memory("a", "s"), 0.9),
                new HybridMatch<T>((T)(object)new Memory("b", "s"), 0.8)
            ]);
        }
    }

    private sealed class SearchlessSession : IDocumentReadOperations
    {
        public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull
            => Task.FromResult<T?>(default);

        public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull
            => Task.FromResult<T?>(default);

        public IQueryable<T> Query<T>() where T : notnull => Array.Empty<T>().AsQueryable();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ⚠️ NOT derived from SearchlessSession, and that is a trap worth naming: the interface mapping
    // is fixed where IDocumentReadOperations appears in the base list, so a base class that takes the
    // throwing default keeps it even when a subclass declares a matching public member. A store that
    // adds search on a derived session type has to re-declare the interface, not just the property.
    private sealed class SearchingSession : IDocumentReadOperations
    {
        public IDocumentSearchOperations Search { get; } = new RecordingSearch();

        public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull
            => Task.FromResult<T?>(default);

        public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull
            => Task.FromResult<T?>(default);

        public IQueryable<T> Query<T>() where T : notnull => Array.Empty<T>().AsQueryable();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
