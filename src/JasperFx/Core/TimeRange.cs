namespace JasperFx.Core;

public enum TimeRangeComparison
{
    Same,
    After,
    Before,
    Overlap
}

public record TimeRange(DateTimeOffset? From, DateTimeOffset? To)
{
    public static TimeRange AllTime() => new TimeRange(null, null);

    public TimeRangeComparison Compare(TimeRange other)
    {
        if (From == other.From && To == other.To) return TimeRangeComparison.Same;

        // this is entirely before other
        if (To.HasValue && other.From.HasValue && To.Value <= other.From.Value)
            return TimeRangeComparison.Before;

        // this is entirely after other
        if (From.HasValue && other.To.HasValue && From.Value >= other.To.Value)
            return TimeRangeComparison.After;

        return TimeRangeComparison.Overlap;
    }

    public TimeRange Merge(TimeRange other)
    {
        var from = (From, other.From) switch
        {
            (null, _) or (_, null) => null,
            _ => From < other.From ? From : other.From
        };

        var to = (To, other.To) switch
        {
            (null, _) or (_, null) => null,
            _ => To > other.To ? To : other.To
        };

        return new TimeRange(from, to);
    }

    public bool GreaterThan(TimeSpan duration)
    {
        if (!From.HasValue || !To.HasValue) return true;
        return (To.Value - From.Value) > duration;
    }

    public static TimeRange Merge(TimeRange[] ranges)
    {
        if (ranges.Length == 0) throw new ArgumentException("Cannot merge an empty array of ranges", nameof(ranges));

        var result = ranges[0];
        for (var i = 1; i < ranges.Length; i++)
        {
            result = result.Merge(ranges[i]);
        }

        return result;
    }
}