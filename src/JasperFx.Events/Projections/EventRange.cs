using System.Diagnostics;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;

namespace JasperFx.Events.Projections;

public enum BatchBehavior
{
    Composite,
    Individual
}

/// <summary>
///     Used to specify then track a range of events by sequence number
///     within the asynchronous projections
/// </summary>
public class EventRange
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    public EventRange(ShardName name, long floor, long ceiling, ISubscriptionAgent agent)
    {
        ShardName = name;
        SequenceFloor = floor;
        SequenceCeiling = ceiling;
        Agent = agent;
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
#pragma warning restore CS8618

    public BatchBehavior BatchBehavior { get; set; } = BatchBehavior.Individual;

    /// <summary>
    /// When running a projection as a composite, you need to create clean
    /// clones of the initial EventRange
    /// </summary>
    /// <param name="leafName">ShardName of the projection about to be executed with this range</param>
    /// <returns></returns>
    public EventRange CloneForExecutionLeaf(ShardName leafName)
    {
        return new EventRange(leafName, SequenceFloor, SequenceCeiling, Agent)
        {
            Events = Events,
            BatchBehavior = BatchBehavior,
            ActiveBatch = ActiveBatch,
            Upstream = Upstream
        };
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
    
    /// <summary>
    /// For composite projections, this would be the active projection batch
    /// </summary>
    public IProjectionBatch? ActiveBatch { get; set; }

    public async ValueTask SliceAsync(IEventSlicer slicer)
    {
        var groups = await slicer.SliceAsync(this);
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
        
        Agent.MarkSkipped(eventSequence);

        await SliceAsync(Slicer);
    }

    private readonly List<ICanWrapEvent> _updates = new();
    public IReadOnlyList<IUpdatedEntity> Updates => _updates.OfType<IUpdatedEntity>().ToList();

    public IReadOnlyList<ICanWrapEvent> AllRecordedActions() => _updates;

    [Obsolete("Will be removed in 2.0")]
    public void MarkUpdated<T>(string tenantId, T entity)
    {
        _updates.Add(new Updated<T>(tenantId, entity, ActionType.Store));
    }
    
    public void MarkSliceAction<TDoc, TId>(string tenantId, EventSlice<TDoc, TId> slice)
    {
        if (slice.Snapshot != null)
        {
            _updates.Add(new Updated<TDoc>(tenantId, slice.Snapshot, slice.ResultingAction));
        }
        else if (slice.ResultingAction == ActionType.Delete || slice.ResultingAction == ActionType.HardDelete)
        {
            _updates.Add(new ProjectionDeleted<TDoc, TId>(slice.Id, tenantId));
        }
        else
        {
            Debug.WriteLine("What?");
        }
        
        
    }

    internal List<ISubscriptionExecution> Upstream { get; set; } = [];
}
