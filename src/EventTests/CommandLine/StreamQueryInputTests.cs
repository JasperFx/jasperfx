using System;
using System.Linq;
using JasperFx.Events;
using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#740 — the <c>stream-query</c> command's flag-to-predicate mapping, validation,
/// aggregate-type resolution and stated ordering, pinned against in-memory queryables with no host
/// or database. Store selection is shared with the sibling commands and covered by
/// <see cref="ProjectionRunSourceTests"/>; the store-side translation of these predicates is the
/// compliance suite's job.
/// </summary>
public class StreamQueryInputTests
{
    private class OrderAggregate;

    private class InvoiceAggregate;

    /// <summary>
    /// Two distinct types sharing the simple name "Widget", for the ambiguity tests. Nested in
    /// separate holders so their full names differ while their simple names collide.
    /// </summary>
    private static class FirstHolder
    {
        public class Widget;
    }

    private static class SecondHolder
    {
        public class Widget;
    }

    private static StreamState state(
        long version = 1,
        long compactedVersion = 0,
        Type? aggregateType = null,
        bool archived = false,
        DateTimeOffset? created = null,
        DateTimeOffset? lastTimestamp = null,
        Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Version = version,
            CompactedVersion = compactedVersion,
            AggregateType = aggregateType,
            IsArchived = archived,
            Created = created ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            LastTimestamp = lastTimestamp ?? created ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
        };

    private static StreamState[] apply(StreamQueryInput input, Type? aggregateType, params StreamState[] states)
        => input.ApplyFilters(states.AsQueryable(), aggregateType).ToArray();

    [Fact]
    public void no_flags_applies_no_filter()
    {
        var states = new[] { state(), state(archived: true), state(version: 100) };

        apply(new StreamQueryInput(), null, states).ShouldBe(states);
        new StreamQueryInput().Validate().ShouldBeNull();
    }

    [Fact]
    public void the_aggregate_type_filter_is_equality_against_the_resolved_type()
    {
        var order = state(aggregateType: typeof(OrderAggregate));
        var invoice = state(aggregateType: typeof(InvoiceAggregate));
        var untyped = state();

        var matched = apply(new StreamQueryInput(), typeof(OrderAggregate), order, invoice, untyped);

        matched.ShouldBe([order]);
    }

    [Fact]
    public void min_version_is_inclusive()
    {
        var below = state(version: 2);
        var exactly = state(version: 3);
        var above = state(version: 4);

        var matched = apply(new StreamQueryInput { MinVersionFlag = 3 }, null, below, exactly, above);

        matched.ShouldBe([exactly, above]);
    }

    /// <summary>
    /// The compaction-policy predicate: growth is measured from the watermark, not from zero, so a
    /// long stream that was recently compacted does not match.
    /// </summary>
    [Fact]
    public void version_above_compacted_measures_growth_since_the_watermark()
    {
        var freshlyCompacted = state(version: 100, compactedVersion: 99);   // growth 1
        var growthAtThreshold = state(version: 8, compactedVersion: 5);     // growth 3, not above
        var overgrown = state(version: 9, compactedVersion: 5);             // growth 4
        var neverCompacted = state(version: 4);                             // growth 4

        var matched = apply(new StreamQueryInput { VersionAboveCompactedFlag = 3 }, null,
            freshlyCompacted, growthAtThreshold, overgrown, neverCompacted);

        // Strictly above the threshold, and raw Version plays no part: the version-100 stream is
        // excluded while the version-4 one matches.
        matched.ShouldBe([overgrown, neverCompacted]);
    }

    [Fact]
    public void the_archived_flag_is_a_tri_state()
    {
        var live = state();
        var archived = state(archived: true);

        apply(new StreamQueryInput(), null, live, archived).ShouldBe([live, archived]);
        apply(new StreamQueryInput { ArchivedFlag = true }, null, live, archived).ShouldBe([archived]);
        apply(new StreamQueryInput { ArchivedFlag = false }, null, live, archived).ShouldBe([live]);
    }

    [Fact]
    public void the_created_window_is_inclusive_and_bounds_created_not_last_append()
    {
        var early = state(created: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        // Created early but appended to recently: the trap for a store or mapping that reads the
        // wrong column.
        var earlyButActive = state(
            created: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            lastTimestamp: new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        var onTheBoundary = state(created: new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));
        var late = state(created: new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));

        var input = new StreamQueryInput
        {
            CreatedFromFlag = "2026-09-05T00:00:00Z", CreatedToFlag = "2026-09-07T00:00:00Z"
        };

        apply(input, null, early, earlyButActive, onTheBoundary, late).ShouldBe([onTheBoundary, late]);
    }

    [Fact]
    public void the_updated_window_is_inclusive_and_bounds_last_append()
    {
        var stale = state(lastTimestamp: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var onTheBoundary = state(lastTimestamp: new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));

        var input = new StreamQueryInput { UpdatedFromFlag = "2026-09-05T00:00:00Z" };

        apply(input, null, stale, onTheBoundary).ShouldBe([onTheBoundary]);
    }

    [Fact]
    public void filters_compose_as_and()
    {
        var target = state(version: 9, compactedVersion: 5, aggregateType: typeof(OrderAggregate));
        var wrongType = state(version: 9, compactedVersion: 5, aggregateType: typeof(InvoiceAggregate));
        var notGrown = state(version: 6, compactedVersion: 5, aggregateType: typeof(OrderAggregate));
        var archived = state(version: 9, compactedVersion: 5, aggregateType: typeof(OrderAggregate),
            archived: true);

        var input = new StreamQueryInput { VersionAboveCompactedFlag = 3, ArchivedFlag = false };

        // The compaction policy's own shape: type AND growth AND not archived; each decoy fails
        // exactly one predicate.
        apply(input, typeof(OrderAggregate), target, wrongType, notGrown, archived).ShouldBe([target]);
    }

    [Fact]
    public void the_ordering_contract_is_creation_order_with_identity_tiebreak()
    {
        var tie = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
        var older = state(created: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var tieLowId = state(created: tie, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var tieHighId = state(created: tie, id: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var newest = state(created: new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));

        var ordered = StreamQueryInput
            .ApplyOrdering(new[] { newest, tieHighId, tieLowId, older }.AsQueryable())
            .ToArray();

        ordered.ShouldBe([older, tieLowId, tieHighId, newest]);
    }

    [Fact]
    public void paging_flags_are_validated()
    {
        new StreamQueryInput { PageFlag = 0 }.Validate().ShouldBe("--page must be 1 or greater");
        new StreamQueryInput { PageSizeFlag = 0 }.Validate().ShouldBe("--page-size must be greater than zero");
    }

    [Fact]
    public void negative_version_thresholds_are_refused()
    {
        new StreamQueryInput { MinVersionFlag = -1 }.Validate().ShouldBe("--min-version cannot be negative");
        new StreamQueryInput { VersionAboveCompactedFlag = -1 }.Validate()
            .ShouldBe("--version-above-compacted cannot be negative");
    }

    [Fact]
    public void unparseable_timestamps_are_refused_with_the_offending_flag_named()
    {
        new StreamQueryInput { CreatedFromFlag = "not-a-time" }.Validate()!.ShouldContain("--created-from");
        new StreamQueryInput { CreatedToFlag = "nope" }.Validate()!.ShouldContain("--created-to");
        new StreamQueryInput { UpdatedFromFlag = "nope" }.Validate()!.ShouldContain("--updated-from");
        new StreamQueryInput { UpdatedToFlag = "nope" }.Validate()!.ShouldContain("--updated-to");
    }

    [Fact]
    public void inverted_windows_are_refused()
    {
        new StreamQueryInput { CreatedFromFlag = "2026-09-02T00:00:00Z", CreatedToFlag = "2026-09-01T00:00:00Z" }
            .Validate().ShouldBe("--created-from must be less than or equal to --created-to");
        new StreamQueryInput { UpdatedFromFlag = "2026-09-02T00:00:00Z", UpdatedToFlag = "2026-09-01T00:00:00Z" }
            .Validate().ShouldBe("--updated-from must be less than or equal to --updated-to");
    }

    [Fact]
    public void aggregate_type_resolves_by_full_name_then_simple_name_case_insensitively()
    {
        Type[] candidates = [typeof(OrderAggregate), typeof(InvoiceAggregate)];

        StreamQueryInput.ResolveAggregateType(typeof(OrderAggregate).FullName!, candidates)
            .ShouldBe((typeof(OrderAggregate), null));
        StreamQueryInput.ResolveAggregateType("orderaggregate", candidates)
            .ShouldBe((typeof(OrderAggregate), null));
    }

    [Fact]
    public void an_unknown_aggregate_type_is_refused_with_guidance()
    {
        var (type, error) = StreamQueryInput.ResolveAggregateType("NoSuchAggregate",
            [typeof(OrderAggregate)]);

        type.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("No loaded type matches --aggregate-type NoSuchAggregate");
    }

    [Fact]
    public void an_ambiguous_simple_name_is_refused_naming_the_candidates()
    {
        // Two distinct types sharing a simple name — the AppDomain scan will hit this for any
        // commonly-named aggregate, and querying the wrong one reads exactly like an empty store.
        var (type, error) = StreamQueryInput.ResolveAggregateType("Widget",
            [typeof(FirstHolder.Widget), typeof(SecondHolder.Widget)]);

        type.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("ambiguous");
        error.ShouldContain(typeof(FirstHolder.Widget).FullName!);
        error.ShouldContain(typeof(SecondHolder.Widget).FullName!);
    }

    [Fact]
    public void a_full_name_disambiguates_a_shared_simple_name()
    {
        var (type, error) = StreamQueryInput.ResolveAggregateType(typeof(SecondHolder.Widget).FullName!,
            [typeof(FirstHolder.Widget), typeof(SecondHolder.Widget)]);

        error.ShouldBeNull();
        type.ShouldBe(typeof(SecondHolder.Widget));
    }

    [Fact]
    public void the_command_name_is_kebab_cased()
    {
        // CommandFactory only strips the "Command" suffix and lowercases, so without the explicit
        // name this would register as "streamquery".
        var attribute = typeof(StreamQueryCommand)
            .GetCustomAttributes(typeof(JasperFx.CommandLine.DescriptionAttribute), false)
            .Cast<JasperFx.CommandLine.DescriptionAttribute>()
            .Single();

        attribute.Name.ShouldBe("stream-query");
    }
}
