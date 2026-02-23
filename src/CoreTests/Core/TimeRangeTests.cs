using JasperFx.Core;
using Shouldly;

namespace CoreTests.Core;

public class TimeRangeTests
{
    private static readonly DateTimeOffset Jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb1 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mar1 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Apr1 = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void same_bounded_ranges()
    {
        var range = new TimeRange(Jan1, Mar1);
        range.Compare(new TimeRange(Jan1, Mar1)).ShouldBe(TimeRangeComparison.Same);
    }

    [Fact]
    public void same_all_time_ranges()
    {
        TimeRange.AllTime().Compare(TimeRange.AllTime()).ShouldBe(TimeRangeComparison.Same);
    }

    [Fact]
    public void same_open_ended_ranges()
    {
        var range = new TimeRange(Jan1, null);
        range.Compare(new TimeRange(Jan1, null)).ShouldBe(TimeRangeComparison.Same);
    }

    [Fact]
    public void same_open_start_ranges()
    {
        var range = new TimeRange(null, Mar1);
        range.Compare(new TimeRange(null, Mar1)).ShouldBe(TimeRangeComparison.Same);
    }

    [Fact]
    public void before_non_overlapping()
    {
        var first = new TimeRange(Jan1, Feb1);
        var second = new TimeRange(Mar1, Apr1);
        first.Compare(second).ShouldBe(TimeRangeComparison.Before);
    }

    [Fact]
    public void before_adjacent()
    {
        var first = new TimeRange(Jan1, Feb1);
        var second = new TimeRange(Feb1, Mar1);
        first.Compare(second).ShouldBe(TimeRangeComparison.Before);
    }

    [Fact]
    public void after_non_overlapping()
    {
        var first = new TimeRange(Mar1, Apr1);
        var second = new TimeRange(Jan1, Feb1);
        first.Compare(second).ShouldBe(TimeRangeComparison.After);
    }

    [Fact]
    public void after_adjacent()
    {
        var first = new TimeRange(Feb1, Mar1);
        var second = new TimeRange(Jan1, Feb1);
        first.Compare(second).ShouldBe(TimeRangeComparison.After);
    }

    [Fact]
    public void overlap_partial()
    {
        var first = new TimeRange(Jan1, Mar1);
        var second = new TimeRange(Feb1, Apr1);
        first.Compare(second).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void overlap_one_contains_other()
    {
        var outer = new TimeRange(Jan1, Apr1);
        var inner = new TimeRange(Feb1, Mar1);
        outer.Compare(inner).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void overlap_inner_vs_outer()
    {
        var inner = new TimeRange(Feb1, Mar1);
        var outer = new TimeRange(Jan1, Apr1);
        inner.Compare(outer).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void overlap_same_start_different_end()
    {
        var first = new TimeRange(Jan1, Feb1);
        var second = new TimeRange(Jan1, Mar1);
        first.Compare(second).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void overlap_different_start_same_end()
    {
        var first = new TimeRange(Jan1, Mar1);
        var second = new TimeRange(Feb1, Mar1);
        first.Compare(second).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void overlap_open_end_with_bounded()
    {
        var open = new TimeRange(Jan1, null);
        var bounded = new TimeRange(Feb1, Mar1);
        open.Compare(bounded).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void overlap_open_start_with_bounded()
    {
        var open = new TimeRange(null, Mar1);
        var bounded = new TimeRange(Jan1, Feb1);
        open.Compare(bounded).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void overlap_all_time_with_bounded()
    {
        var allTime = TimeRange.AllTime();
        var bounded = new TimeRange(Jan1, Feb1);
        allTime.Compare(bounded).ShouldBe(TimeRangeComparison.Overlap);
    }

    [Fact]
    public void before_with_open_start()
    {
        var open = new TimeRange(null, Jan1);
        var bounded = new TimeRange(Feb1, Mar1);
        open.Compare(bounded).ShouldBe(TimeRangeComparison.Before);
    }

    [Fact]
    public void after_with_open_end()
    {
        var open = new TimeRange(Mar1, null);
        var bounded = new TimeRange(Jan1, Feb1);
        open.Compare(bounded).ShouldBe(TimeRangeComparison.After);
    }

    // Merge tests

    [Fact]
    public void merge_non_overlapping_ranges()
    {
        var first = new TimeRange(Jan1, Feb1);
        var second = new TimeRange(Mar1, Apr1);
        first.Merge(second).ShouldBe(new TimeRange(Jan1, Apr1));
    }

    [Fact]
    public void merge_overlapping_ranges()
    {
        var first = new TimeRange(Jan1, Mar1);
        var second = new TimeRange(Feb1, Apr1);
        first.Merge(second).ShouldBe(new TimeRange(Jan1, Apr1));
    }

    [Fact]
    public void merge_contained_range()
    {
        var outer = new TimeRange(Jan1, Apr1);
        var inner = new TimeRange(Feb1, Mar1);
        outer.Merge(inner).ShouldBe(new TimeRange(Jan1, Apr1));
    }

    [Fact]
    public void merge_same_ranges()
    {
        var range = new TimeRange(Jan1, Mar1);
        range.Merge(new TimeRange(Jan1, Mar1)).ShouldBe(new TimeRange(Jan1, Mar1));
    }

    [Fact]
    public void merge_with_open_end()
    {
        var first = new TimeRange(Jan1, Feb1);
        var second = new TimeRange(Mar1, null);
        first.Merge(second).ShouldBe(new TimeRange(Jan1, null));
    }

    [Fact]
    public void merge_with_open_start()
    {
        var first = new TimeRange(null, Feb1);
        var second = new TimeRange(Jan1, Mar1);
        first.Merge(second).ShouldBe(new TimeRange(null, Mar1));
    }

    [Fact]
    public void merge_both_open_ends()
    {
        var first = new TimeRange(Jan1, null);
        var second = new TimeRange(Feb1, null);
        first.Merge(second).ShouldBe(new TimeRange(Jan1, null));
    }

    [Fact]
    public void merge_both_open_starts()
    {
        var first = new TimeRange(null, Feb1);
        var second = new TimeRange(null, Mar1);
        first.Merge(second).ShouldBe(new TimeRange(null, Mar1));
    }

    [Fact]
    public void merge_with_all_time()
    {
        var bounded = new TimeRange(Jan1, Feb1);
        bounded.Merge(TimeRange.AllTime()).ShouldBe(TimeRange.AllTime());
    }

    [Fact]
    public void merge_is_commutative()
    {
        var first = new TimeRange(Jan1, Mar1);
        var second = new TimeRange(Feb1, Apr1);
        first.Merge(second).ShouldBe(second.Merge(first));
    }

    // Static Merge tests

    [Fact]
    public void static_merge_single_range()
    {
        var range = new TimeRange(Jan1, Mar1);
        TimeRange.Merge([range]).ShouldBe(range);
    }

    [Fact]
    public void static_merge_two_ranges()
    {
        var result = TimeRange.Merge([new TimeRange(Jan1, Feb1), new TimeRange(Mar1, Apr1)]);
        result.ShouldBe(new TimeRange(Jan1, Apr1));
    }

    [Fact]
    public void static_merge_multiple_ranges_left_to_right()
    {
        var result = TimeRange.Merge([
            new TimeRange(Jan1, Feb1),
            new TimeRange(Feb1, Mar1),
            new TimeRange(Mar1, Apr1)
        ]);
        result.ShouldBe(new TimeRange(Jan1, Apr1));
    }

    [Fact]
    public void static_merge_with_open_end_in_middle()
    {
        var result = TimeRange.Merge([
            new TimeRange(Jan1, Feb1),
            new TimeRange(Mar1, null),
            new TimeRange(Feb1, Apr1)
        ]);
        result.ShouldBe(new TimeRange(Jan1, null));
    }

    [Fact]
    public void static_merge_with_open_start_first()
    {
        var result = TimeRange.Merge([
            new TimeRange(null, Feb1),
            new TimeRange(Jan1, Mar1),
            new TimeRange(Feb1, Apr1)
        ]);
        result.ShouldBe(new TimeRange(null, Apr1));
    }

    [Fact]
    public void static_merge_empty_array_throws()
    {
        Should.Throw<ArgumentException>(() => TimeRange.Merge([]));
    }
}
