using JasperFx.Events.Vectors;
using Shouldly;

namespace EventTests.Vectors;

// The shared fusion helper (jasperfx#844). Each store had a private copy that could only fuse one
// document type with itself; these are the facts every copy was supposed to hold and that only a
// shared one can be held to.
public class ReciprocalRankFusionTests
{
    private sealed record Doc(string Id, string Title);

    private static Doc D(string id) => new(id, id.ToUpperInvariant());

    [Fact]
    public void agreement_between_legs_outranks_a_single_leg_winner()
    {
        // "b" is second in both legs; "a" is first in one and absent from the other.
        var fused = ReciprocalRankFusion.Fuse(
            [D("a"), D("b")],
            [D("c"), D("b")],
            x => x.Id,
            limit: 3);

        fused.Select(x => x.Document.Id).ShouldBe(["b", "a", "c"]);
    }

    [Fact]
    public void score_is_the_sum_of_reciprocals_over_the_legs_that_found_it()
    {
        var fused = ReciprocalRankFusion.Fuse([D("a")], [D("a")], x => x.Id, limit: 1, k: 60);

        // Rank 1 in both legs: 1/61 + 1/61.
        fused.Single().Score.ShouldBe(2.0 / 61, 1e-12);
    }

    [Fact]
    public void ranks_are_one_based_so_the_top_of_a_leg_is_not_divided_by_k_alone()
    {
        var fused = ReciprocalRankFusion.Fuse([[D("a")]], x => x.Id, limit: 1, k: 60);

        fused.Single().Score.ShouldBe(1.0 / 61, 1e-12);
    }

    [Fact]
    public void reads_ordinal_position_only_so_the_legs_need_no_common_scale()
    {
        // Same ordering, wildly different underlying relevance/distance scales: identical result.
        var first = ReciprocalRankFusion.Fuse([D("a"), D("b")], [D("b"), D("a")], x => x.Id, limit: 2);
        var second = ReciprocalRankFusion.Fuse([D("a"), D("b")], [D("b"), D("a")], x => x.Id, limit: 2);

        first.Select(x => x.Score).ShouldBe(second.Select(x => x.Score));
    }

    [Fact]
    public void fuses_across_document_types_by_key()
    {
        // The shape a vector projection writes: the text leg ranks snapshots, the vector leg ranks a
        // separate embedding document. No store's private copy could do this at all.
        var textLeg = new[] { ("a", "snapshot"), ("b", "snapshot") }.Select(x => x.Item1).ToList();
        var vectorLeg = new[] { "b", "c" }.ToList();

        var fused = ReciprocalRankFusion.Fuse([textLeg, vectorLeg], x => x, limit: 3);

        fused.Select(x => x.Document).ShouldBe(["b", "a", "c"]);
    }

    [Fact]
    public void ties_break_deterministically_rather_than_by_dictionary_order()
    {
        // "x" and "y" are both found once, at rank 1 of their own leg — identical scores.
        var first = ReciprocalRankFusion.Fuse([D("y")], [D("x")], d => d.Id, limit: 2);
        var second = ReciprocalRankFusion.Fuse([D("x")], [D("y")], d => d.Id, limit: 2);

        first.Select(x => x.Document.Id).ShouldBe(["x", "y"]);
        second.Select(x => x.Document.Id).ShouldBe(["x", "y"]);
    }

    [Fact]
    public void keeps_the_first_legs_instance_for_a_document_found_in_both()
    {
        var fromText = new Doc("a", "from-text");
        var fromVector = new Doc("a", "from-vector");

        var fused = ReciprocalRankFusion.Fuse([fromText], [fromVector], x => x.Id, limit: 1);

        fused.Single().Document.ShouldBeSameAs(fromText);
    }

    [Fact]
    public void limit_truncates_the_fused_ranking_rather_than_each_leg()
    {
        Doc[] textLeg = [D("a"), D("b"), D("c")];
        Doc[] vectorLeg = [D("c"), D("b"), D("a")];

        var full = ReciprocalRankFusion.Fuse(textLeg, vectorLeg, x => x.Id, limit: 10);
        var one = ReciprocalRankFusion.Fuse(textLeg, vectorLeg, x => x.Id, limit: 1);

        full.Count.ShouldBe(3);
        one.Count.ShouldBe(1);
        one.Single().Document.Id.ShouldBe(full[0].Document.Id);
    }

    [Fact]
    public void a_first_place_finish_outweighs_being_consistently_second()
    {
        // ⚠️ Worth knowing before tuning k: at k = 60 the curve is nearly flat, so 1/61 + 1/63 (first
        // and last) very slightly BEATS 1/62 + 1/62 (second in both). RRF rewards agreement, but not
        // enough to overtake a document that topped one leg. Lowering k sharpens the difference.
        var fused = ReciprocalRankFusion.Fuse(
            [D("a"), D("b"), D("c")],
            [D("c"), D("b"), D("a")],
            x => x.Id,
            limit: 3);

        fused.Select(x => x.Document.Id).ShouldBe(["a", "c", "b"]);
        fused[0].Score.ShouldBeGreaterThan(fused[2].Score);
    }

    [Fact]
    public void an_empty_leg_is_simply_the_other_legs_ranking()
    {
        var fused = ReciprocalRankFusion.Fuse([D("a"), D("b")], [], x => x.Id, limit: 5);

        fused.Select(x => x.Document.Id).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void refuses_a_k_of_zero_rather_than_scoring_a_top_hit_infinitely()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            ReciprocalRankFusion.Fuse([D("a")], [], x => x.Id, limit: 1, k: 0));
    }

    [Fact]
    public void refuses_a_limit_below_one()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            ReciprocalRankFusion.Fuse([D("a")], [], x => x.Id, limit: 0));
    }
}

// The shared options record (jasperfx#840). Its defaults ARE the behavior the three stores were
// disagreeing about, so they are asserted rather than assumed.
public class HybridSearchOptionsTests
{
    [Fact]
    public void distance_defaults_to_the_index_declaration_rather_than_to_cosine()
    {
        // ⚠️ The regression this type exists to prevent: Marten's own options defaulted this to
        // Cosine, so an index declared L2 was searched by cosine while Polecat and Fisher searched by
        // L2 — same code, three stores, different answers, nothing reported.
        new HybridSearchOptions().Distance.ShouldBeNull();
    }

    [Fact]
    public void defaults_match_the_shape_every_store_had_settled_on()
    {
        var options = new HybridSearchOptions();

        options.K.ShouldBe(ReciprocalRankFusion.DefaultK);
        options.CandidateDepth.ShouldBeNull();
        options.TextStyle.ShouldBe(HybridTextStyle.PlainText);
        options.RegConfig.ShouldBeNull();
        options.ColumnWeights.ShouldBeNull();
    }

    [Fact]
    public void candidate_depth_defaults_deeper_than_the_limit()
    {
        new HybridSearchOptions().ResolveCandidateDepth(10).ShouldBe(50);
        new HybridSearchOptions().ResolveCandidateDepth(100).ShouldBe(400);
    }

    [Fact]
    public void an_explicit_candidate_depth_is_taken_as_given()
    {
        new HybridSearchOptions(CandidateDepth: 250).ResolveCandidateDepth(10).ShouldBe(250);
    }

    [Fact]
    public void refuses_a_candidate_depth_below_the_limit()
    {
        // Reading only `limit` per leg defeats the point: a document ranked 40th by one leg and first
        // by the other is what a hybrid search is for.
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new HybridSearchOptions(CandidateDepth: 5).ResolveCandidateDepth(10));
    }

    [Fact]
    public void refuses_a_k_of_zero_and_a_limit_below_one()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new HybridSearchOptions(K: 0).ResolveCandidateDepth(10));
        Should.Throw<ArgumentOutOfRangeException>(() => new HybridSearchOptions().ResolveCandidateDepth(0));
    }

    // Per-column weights for the text leg (jasperfx#854, for fisher#289). Only a store that ranks per
    // column at query time can honour them, so the refusals live here rather than in that one store.
    [Fact]
    public void no_column_weights_means_every_column_weighs_the_same()
    {
        new HybridSearchOptions().ResolveColumnWeights(3).ShouldBeNull();
    }

    [Fact]
    public void column_weights_matching_the_index_are_taken_as_given()
    {
        new HybridSearchOptions(ColumnWeights: [3.0, 1.0, 2.0])
            .ResolveColumnWeights(3)
            .ShouldBe([3.0, 1.0, 2.0]);
    }

    [Fact]
    public void refuses_a_weight_per_column_count_that_does_not_match_the_index()
    {
        // ⚠️ Refused rather than padded: padding weighs the forgotten columns at 1.0 and returns a
        // ranking that is quietly not the one the caller asked for.
        var tooFew = Should.Throw<ArgumentException>(() =>
            new HybridSearchOptions(ColumnWeights: [3.0, 1.0]).ResolveColumnWeights(3));
        tooFew.Message.ShouldContain("2 weights");
        tooFew.Message.ShouldContain("3 column(s)");

        Should.Throw<ArgumentException>(() =>
            new HybridSearchOptions(ColumnWeights: [3.0, 1.0, 2.0, 1.0]).ResolveColumnWeights(3));
    }

    [Fact]
    public void refuses_an_empty_weights_array_rather_than_reading_it_as_the_default()
    {
        // An empty array is a caller who built the weights and got none, not a caller who wants the
        // default — null is how you ask for the default.
        Should.Throw<ArgumentException>(() =>
            new HybridSearchOptions(ColumnWeights: []).ResolveColumnWeights(3));
    }

    [Fact]
    public void refuses_a_weight_that_cannot_rank_anything()
    {
        Should.Throw<ArgumentException>(() =>
            new HybridSearchOptions(ColumnWeights: [1.0, double.NaN, 2.0]).ResolveColumnWeights(3))
            .Message.ShouldContain("ColumnWeights[1]");

        Should.Throw<ArgumentException>(() =>
            new HybridSearchOptions(ColumnWeights: [double.PositiveInfinity, 1.0, 2.0])
                .ResolveColumnWeights(3));
    }

    [Fact]
    public void a_negative_weight_is_allowed_because_it_means_something()
    {
        // bm25 takes any finite weight; a negative one makes a column count AGAINST a document. Odd,
        // but well-defined, so it is not ours to refuse.
        new HybridSearchOptions(ColumnWeights: [1.0, -1.0]).ResolveColumnWeights(2)
            .ShouldBe([1.0, -1.0]);
    }

    [Fact]
    public void a_store_that_cannot_weight_columns_refuses_them_by_name()
    {
        // ⚠️ The whole point of putting this on the SHARED record: a store with nowhere to put the
        // weights must say so, because a silently unweighted ranking still looks like an answer.
        new HybridSearchOptions().AssertColumnWeightsAreNotSupported("Marten", "Use WeightedFullTextIndex.");

        Should.Throw<NotSupportedException>(() =>
                new HybridSearchOptions(ColumnWeights: [3.0, 1.0])
                    .AssertColumnWeightsAreNotSupported("Marten", "Use WeightedFullTextIndex."))
            .Message.ShouldContain("Use WeightedFullTextIndex.");
    }
}
