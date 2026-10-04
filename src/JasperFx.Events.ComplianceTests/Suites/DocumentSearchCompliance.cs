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
/// <para>
/// <b>The hierarchy facts (jasperfx#944) assert a member, not a count.</b> A search materializes its
/// rows as the <c>T</c> that was asked for, so a store with no discriminator returns the right NUMBER
/// of wrong objects whenever the hierarchy holds one row per type — siblings deserialized as the
/// requested sub-class with that sub-class's own members at their defaults. Those facts are gated on
/// <see cref="DocumentStorageComplianceFixture.SupportsDocumentHierarchies" /> as well as the search
/// gates. Whether a search for the ROOT resolves each row to its concrete type the way
/// <c>Query&lt;TRoot&gt;()</c> does is deliberately not pinned: no store's search path does it yet.
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

        // jasperfx#944. Replayed only by fixtures with SupportsDocumentHierarchies; the facts that
        // need it are gated on that flag.
        config.AddSubClass<ComplianceMemory, ComplianceNote>();
        config.AddSubClass<ComplianceMemory, ComplianceTranscript>();
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

    private static readonly Guid TranscriptId = Guid.NewGuid();
    private static readonly Guid NearNoteId = Guid.NewGuid();
    private static readonly Guid PlainId = Guid.NewGuid();
    private static readonly Guid FarNoteId = Guid.NewGuid();

    // jasperfx#944. Ordered nearest-to-furthest from TowardsAlpha under every metric, and the
    // SIBLING is deliberately the nearest of all: a store with no discriminator answers a search for
    // ComplianceNote with the transcript FIRST, rather than with the right notes plus extras.
    private async Task theHierarchyAsync()
    {
        // Each stored under its own static type, as the diagnostics suite's hierarchy facts do.
        await using var session = LightweightSession();
        session.Store(new ComplianceTranscript
        {
            Id = TranscriptId, Body = "the fox in the snow", Scope = "wanted", Embedding = [1f, 0f, 0f],
            Speakers = 4
        });
        session.Store(new ComplianceNote
        {
            Id = NearNoteId, Body = "a fox at dusk", Scope = "wanted", Embedding = [0.8f, 0.6f, 0f],
            Author = "ursula"
        });
        session.Store(new ComplianceMemory
        {
            Id = PlainId, Body = "fox tracks by the river", Scope = "wanted", Embedding = [0.6f, 0.8f, 0f]
        });
        session.Store(new ComplianceNote
        {
            Id = FarNoteId, Body = "unrelated weather notes", Scope = "other", Embedding = [0f, 0f, 1f],
            Author = "gene"
        });
        await session.SaveChangesAsync(Cancellation);
    }

    private void SkipUnlessHierarchies()
        => Assert.SkipUnless(theFixture.SupportsDocumentHierarchies,
            "This fixture does not replay DocumentComplianceConfig.SubClasses.");

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

    [Fact]
    public async Task a_vector_search_for_a_sub_class_returns_only_that_sub_class()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");
        SkipUnlessHierarchies();

        await theHierarchyAsync();

        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceNote>(
            x => x.Embedding, TowardsAlpha, limit: 4, token: Cancellation);

        // The transcript is the nearest row in the table and the plain memory the third; neither is
        // a note. Ids first, so a failure names the intruder...
        hits.Select(x => x.Document.Id).ShouldBe([NearNoteId, FarNoteId]);

        // ...and then a member only a note carries, because a store with no discriminator hands
        // siblings back AS notes, with Author at its default.
        hits.Select(x => x.Document.Author).ShouldBe(["ursula", "gene"]);
        hits.Select(x => x.Distance).ShouldBeInOrder();
    }

    [Fact]
    public async Task a_vector_search_for_the_root_returns_the_whole_hierarchy_nearest_first()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");
        SkipUnlessHierarchies();

        await theHierarchyAsync();

        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceMemory>(
            x => x.Embedding, TowardsAlpha, limit: 4, token: Cancellation);

        // Ids only. Whether each row comes back as its concrete sub-class, the way Query<TRoot>()
        // resolves it, is deliberately not pinned — see the remarks on the class.
        hits.Select(x => x.Document.Id).ShouldBe([TranscriptId, NearNoteId, PlainId, FarNoteId]);
        hits.Select(x => x.Distance).ShouldBeInOrder();
    }

    [Fact]
    public async Task a_hybrid_search_for_a_sub_class_returns_only_that_sub_class()
    {
        Assert.SkipUnless(theFixture.SupportsHybridSearch,
            "This store does not implement hybrid search.");
        SkipUnlessHierarchies();

        await theHierarchyAsync();

        await using var query = QuerySession();
        var hits = await query.Search.HybridSearchWithScoresAsync<ComplianceNote>(
            x => x.Embedding, "fox", TowardsAlpha, limit: 4, token: Cancellation);

        // Both legs read the same table. The transcript carries the term AND is nearest the query
        // vector, and the plain memory carries the term too, so a discriminator on only one leg lets
        // them back in through the other.
        hits.ShouldNotBeEmpty();
        hits.Select(x => x.Document.Id).ShouldBeSubsetOf([NearNoteId, FarNoteId]);
        hits.Select(x => x.Document.Id).ShouldContain(NearNoteId);
        hits.ShouldAllBe(x => x.Document.Author != string.Empty);
    }

    [Fact]
    public async Task a_sub_class_search_takes_a_filter_on_the_sub_class_own_members()
    {
        Assert.SkipUnless(theFixture.SupportsVectorSearch,
            "This store does not implement IDocumentReadOperations.Search.");
        SkipUnlessHierarchies();

        await theHierarchyAsync();

        // Author is declared on ComplianceNote, not on the root whose table it is stored in. The
        // far note is the furthest row of the whole hierarchy and must still be the answer: the
        // filter and the discriminator both narrow before the limit.
        await using var query = QuerySession();
        var hits = await query.Search.VectorSearchWithScoresAsync<ComplianceNote>(
            x => x.Embedding, TowardsAlpha, limit: 1,
            filter: x => x.Author == "gene", token: Cancellation);

        hits.Count.ShouldBe(1);
        hits[0].Document.Id.ShouldBe(FarNoteId);
        hits[0].Document.Author.ShouldBe("gene");
    }
}
