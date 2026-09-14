using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using JasperFx.Events.Vectors;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// <see cref="IDocumentSearchOperations" />, reached through
/// <see cref="IDocumentReadOperations.Search" /> (jasperfx#842), and the filter contract
/// (jasperfx#843).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not a relevance suite, and must not become one.</strong> How good a store's
/// ranking is depends on its tokenizer, its index parameters and the embedding model in play, none of
/// which are shared. What is asserted here is the handful of things a store-agnostic caller has no
/// way to discover for itself and would otherwise have to test per store:
/// </para>
/// <list type="bullet">
///   <item>nearest comes first, and the score is a DISTANCE on every metric</item>
///   <item>the store's own implicit predicates apply, exactly as they do to <c>Query&lt;T&gt;()</c></item>
///   <item>a filter narrows BEFORE the limit, so the result is the top-k of the filtered set</item>
///   <item>a type with no declared index is refused rather than answered wrongly</item>
///   <item>the document-only form is the scored form's documents, in the same order</item>
/// </list>
/// <para>
/// ⚠️ <b>Every one of these was already divergent somewhere when the suite was written</b>, which is
/// the argument for it existing at all: fisher#285 ignored conjoined tenancy and the hierarchy filter
/// outright, and neither Marten.PgVector search had a soft-delete predicate while Polecat and Fisher
/// both did. A capability every store is assumed to share, with nothing shared holding any of them to
/// it, is exactly where these live.
/// </para>
/// <para>
/// <b>The embeddings are hand-written, not generated.</b> A suite that called a model would be
/// testing the model. Three-dimensional unit-ish vectors along the axes make "nearest" a fact anyone
/// can check by reading, and they behave the same under cosine, L2 and inner product ordering.
/// </para>
/// </remarks>
public abstract class DocumentSearchCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_search";
        config.AddDocumentType<ComplianceMemory>();
        config.AddVectorIndex<ComplianceMemory>(nameof(ComplianceMemory.Embedding), 3);
        config.AddFullTextIndex<ComplianceMemory>(nameof(ComplianceMemory.Body));
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    /// <summary>The query vector: the "x" axis. <see cref="Alpha" /> is exactly it.</summary>
    private static readonly float[] TowardsAlpha = [1f, 0f, 0f];

    private static readonly Guid AlphaId = Guid.NewGuid();
    private static readonly Guid BravoId = Guid.NewGuid();
    private static readonly Guid CharlieId = Guid.NewGuid();

    // Ordered nearest-to-furthest from TowardsAlpha under every metric the contract names.
    private static ComplianceMemory Alpha => new()
    {
        Id = AlphaId, Body = "the fox in the snow", Scope = "wanted", Embedding = [1f, 0f, 0f]
    };

    private static ComplianceMemory Bravo => new()
    {
        Id = BravoId, Body = "a fox at dusk", Scope = "other", Embedding = [0.8f, 0.6f, 0f]
    };

    private static ComplianceMemory Charlie => new()
    {
        Id = CharlieId, Body = "unrelated weather notes", Scope = "wanted", Embedding = [0f, 0f, 1f]
    };

    private Task theMemoriesAsync() => PersistAsync(Alpha, Bravo, Charlie);

    [Fact]
    public async Task nearest_comes_first_and_the_score_is_a_distance()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 3, token: Cancellation);

        hits.Count.ShouldBe(3);
        hits[0].Document.Id.ShouldBe(AlphaId);

        // Smaller is closer, on every store and every metric — the one thing a caller cannot
        // discover from the type and would otherwise have to know per store.
        hits.Select(x => x.Distance).ShouldBeInOrder();
        hits[0].Distance.ShouldBeLessThan(hits[2].Distance);
    }

    [Fact]
    public async Task limit_returns_only_the_nearest_documents()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 1, token: Cancellation);

        hits.Count.ShouldBe(1);
        hits[0].Document.Id.ShouldBe(AlphaId);
    }

    [Fact]
    public async Task the_document_only_form_is_the_scored_form_in_the_same_order()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var scored = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 3, token: Cancellation);
        var documents = await query.Search.VectorSearchAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 3, token: Cancellation);

        documents.Select(x => x.Id).ShouldBe(scored.Select(x => x.Document.Id));
    }

    [Fact]
    public async Task a_filter_narrows_before_the_limit()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        await theMemoriesAsync();

        // ⚠️ THE fact of jasperfx#843. Bravo is the second-nearest overall and is excluded; Charlie
        // is the FURTHEST of the three and must still be returned, because the top-2 of the filtered
        // set is what was asked for. A store filtering after the fact returns one row here.
        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 2,
            filter: x => x.Scope == "wanted", token: Cancellation);

        hits.Count.ShouldBe(2);
        hits.Select(x => x.Document.Id).ShouldBe([AlphaId, CharlieId]);
    }

    [Fact]
    public async Task a_filter_excluding_the_nearest_row_still_returns_matches()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 1,
            filter: x => x.Scope == "other", token: Cancellation);

        // Over-reading and discarding returns EMPTY here whenever the over-read does not happen to
        // reach far enough, which is the failure mode the parameter exists to remove.
        hits.Count.ShouldBe(1);
        hits[0].Document.Id.ShouldBe(BravoId);
    }

    [Fact]
    public async Task a_filter_matching_nothing_returns_empty_rather_than_the_unfiltered_top_k()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 3,
            filter: x => x.Scope == "nothing-has-this", token: Cancellation);

        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_search_over_an_empty_document_type_returns_empty()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 5, token: Cancellation);

        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_query_vector_of_the_wrong_length_is_refused()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");

        // Refused rather than answered, and refused by NAME rather than by whatever the database
        // says about a cast. The declared dimensions are 3.
        await using var query = QuerySession();

        var ex = await Should.ThrowAsync<Exception>(async () =>
            await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
                x => x.Embedding, new float[] { 1f, 0f }, limit: 1, token: Cancellation));

        ex.ShouldNotBeOfType<NullReferenceException>();
        ex.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task hybrid_search_returns_the_union_of_both_legs_best_first()
    {
        Assert.SkipUnless(theFixture.SupportsHybridSearch,
            "This store does not implement hybrid search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var hits = await query.Search.HybridSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, "fox", TowardsAlpha, limit: 3, token: Cancellation);

        hits.ShouldNotBeEmpty();

        // ⚠️ THE union fact, and it is deliberately about WHICH SET comes first rather than which
        // document. Alpha and Bravo each carry the term and each sit near the query vector, so both
        // rank in both legs; Charlie ranks in the vector leg only. Reciprocal rank fusion puts a
        // document scored by two legs above one scored by a single leg, and THAT is what a caller
        // can rely on without knowing the store.
        //
        // ⚠️ <b>It used to assert hits[0] is Alpha, on the reasoning that Alpha tops both legs. It
        // does not, and asserting it was a coin flip.</b> BM25 divides by document length, so the
        // SHORTER of two documents carrying the term once outranks the longer — Bravo tops the text
        // leg on every BM25 store while Alpha tops the vector leg, which leaves the two EXACTLY tied
        // under RRF (measured on Fisher: 0.032522 each). Fuse then falls through to its last
        // tie-break, Key.ToString() ordinal — and the keys here are fresh Guids, so the fact passed
        // or failed at random. It was caught by the same run passing under one target framework and
        // failing under the other.
        //
        // Restating it in terms of the union rather than making the corpus break the tie is the
        // deliberate choice: which document a fused ranking puts first when the two legs disagree is
        // a RELEVANCE question, decided by the tokenizer and the ranking parameters, and this suite
        // says at the top that it is not a relevance suite and must not become one.
        hits.Count.ShouldBeGreaterThanOrEqualTo(2);
        hits.Take(2).Select(x => x.Document.Id).ShouldBe([AlphaId, BravoId], ignoreOrder: true);

        // ⚠️ Larger is better here, the OPPOSITE of VectorMatch<T>.Distance. Getting this backwards
        // is silent: the results are still documents, just the worst ones.
        hits.Select(x => x.Score).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task hybrid_search_takes_the_same_filter()
    {
        Assert.SkipUnless(theFixture.SupportsHybridSearch,
            "This store does not implement hybrid search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var hits = await query.Search.HybridSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, "fox", TowardsAlpha, limit: 3,
            filter: x => x.Scope == "wanted", token: Cancellation);

        // Applied to BOTH legs: Bravo carries the term and is near the query vector, so a filter
        // reaching only one leg lets it back in through the other.
        hits.ShouldNotContain(x => x.Document.Id == BravoId);
    }

    [Fact]
    public async Task the_document_only_hybrid_form_is_the_scored_form_in_the_same_order()
    {
        Assert.SkipUnless(theFixture.SupportsHybridSearch,
            "This store does not implement hybrid search.");

        await theMemoriesAsync();

        await using var query = QuerySession();
        var scored = await query.Search.HybridSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, "fox", TowardsAlpha, limit: 3, token: Cancellation);
        var documents = await query.Search.HybridSearchAsync<ComplianceMemory>(
            x => x.Embedding, "fox", TowardsAlpha, limit: 3, token: Cancellation);

        documents.Select(x => x.Id).ShouldBe(scored.Select(x => x.Document.Id));
    }
}
