using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;

namespace JasperFx.Events.Projections;

/// <summary>
///     Used to specify then track a range of events by sequence number
///     within the asynchronous projections
/// </summary>
public class EventRange
{
    public EventRange(ShardName name, long floor, long ceiling)
    {
        ShardName = name;
        SequenceFloor = floor;
        SequenceCeiling = ceiling;
    }
    
    public EventRange(ISubscriptionAgent agent, long floor, long ceiling)
    {
        ShardName = agent.Name;
        Agent = agent;
        SequenceFloor = floor;
        SequenceCeiling = ceiling;
    }

    public EventRange(ISubscriptionAgent agent, long ceiling)
    {
        ShardName = agent.Name;
        Agent = agent;
        SequenceCeiling = ceiling;
    }
    
    public EventRange(ShardName shardName, long ceiling)
    {
        ShardName = shardName;
        SequenceCeiling = ceiling;
    }

    /// <summary>
    ///     Identifies the projection shard consuming this event range
    /// </summary>
    public ShardName ShardName { get; }

    /// <summary>
    ///     The non-inclusive lower bound of the event sequence numbers
    ///     in this range
    /// </summary>
    public long SequenceFloor { get; }

    /// <summary>
    ///     The inclusive upper bound of the event sequence numbers in this range
    /// </summary>
    public long SequenceCeiling { get; }

    /// <summary>
    ///     The actual events fetched for this range and the base filters of the projection
    ///     shard
    /// </summary>
    public List<IEvent> Events { get; set; }

    /// <summary>
    ///     The actual number of events in this range
    /// </summary>
    public int Size => Events?.Count ?? (int)(SequenceCeiling - SequenceFloor);

    public ISubscriptionAgent Agent { get; }

    public IEventSlicer Slicer { get; private set; } = new NulloEventSlicer();

    public async ValueTask SliceAsync(IEventSlicer slicer)
    {
        var groups = await slicer.SliceAsync(Events);
        _groups.Clear();
        _groups.AddRange(groups);
        Slicer = slicer;
    }

    private readonly List<object> _groups = new();


    public IReadOnlyList<object> Groups => _groups;

    protected bool Equals(EventRange other)
    {
        return Equals(ShardName, other.ShardName) && SequenceFloor == other.SequenceFloor &&
               SequenceCeiling == other.SequenceCeiling;
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((EventRange)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = ShardName != null ? ShardName.GetHashCode() : 0;
            hashCode = (hashCode * 397) ^ SequenceFloor.GetHashCode();
            hashCode = (hashCode * 397) ^ SequenceCeiling.GetHashCode();
            return hashCode;
        }
    }

    public override string ToString()
    {
        return $"Event range of '{ShardName}', {SequenceFloor} to {SequenceCeiling}";
    }

    public async ValueTask SkipEventSequence(long eventSequence)
    {
        var events = Events.ToList();
        events.RemoveAll(e => e.Sequence == eventSequence);
        Events = events;

        await SliceAsync(Slicer);
    }

}
