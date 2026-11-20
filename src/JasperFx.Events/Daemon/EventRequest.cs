using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class EventRequest
{
    public long Floor { get; init; }
    public long HighWater { get; init; }
    public int BatchSize { get; init; }

    public ShardName Name { get; init; } = null!;

    public ErrorHandlingOptions ErrorOptions { get; init; } = null!;

    public IDaemonRuntime Runtime { get; init; } = null!;
    public ISubscriptionMetrics Metrics { get; init; } = null!;

    /// <summary>
    /// marten#6003: the shard parks the stream of an event the store can't deserialize. A loader that sees this
    /// reports such an event on <see cref="EventPage.Unreadable" /> -- with the row's stream id or key and tenant --
    /// instead of throwing or dead-lettering it, and counts it as skipped for <see cref="EventPage.CalculateCeiling" />.
    /// Only set for a single-stream aggregation whose <see cref="ErrorHandlingOptions.SerializationErrors" /> is
    /// <see cref="ProjectionErrorAction.Park" />, on a store that parks.
    /// </summary>
    public bool ParksUnreadableEvents { get; init; }

    public override string ToString()
    {
        return $"{nameof(Floor)}: {Floor}, {nameof(HighWater)}: {HighWater}, {nameof(BatchSize)}: {BatchSize}";
    }

    protected bool Equals(EventRequest other)
    {
        return Floor == other.Floor && HighWater == other.HighWater && BatchSize == other.BatchSize;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != this.GetType())
        {
            return false;
        }

        return Equals((EventRequest)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Floor, HighWater, BatchSize);
    }
}
