using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

public enum ShardMode
{
    none,
    continuous,
    rebuilding
}

/// <summary>
///     Point in time state of a single projection shard or the high water mark
/// </summary>
public class ShardState
{
    public const string HighWaterMark = "HighWaterMark";
    public const string AllProjections = "AllProjections";

    public ShardState(string shardName, long sequence)
    {
        ShardName = shardName;
        Sequence = sequence;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public ShardState(ShardName shardName, long sequence): this(shardName.Identity, sequence)
    {
    }

    public long RebuildThreshold { get; set; }

    public ShardMode Mode { get; set; } = ShardMode.continuous;

    public int AssignedNodeNumber { get; set; } = 0;

    public ShardAction Action { get; set; } = ShardAction.Updated;

    /// <summary>
    ///     Time this state was recorded
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    ///     Name of the projection shard
    /// </summary>
    public string ShardName { get; }

    /// <summary>
    ///     Furthest event sequence number processed by this projection shard
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    ///     If not null, this is the exception that caused this state to be published
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// Marks the previous "good" high water mark when the ShardState is publishing a "skipping"
    /// event
    /// </summary>
    public long PreviousGoodMark { get; set; }

    /// <summary>
    /// Last time the shard successfully advanced its position
    /// </summary>
    public DateTimeOffset? LastAdvanced { get; set; }

    /// <summary>
    /// Last heartbeat received from the shard's subscription agent
    /// </summary>
    public DateTimeOffset? LastHeartbeat { get; set; }

    /// <summary>
    /// Current status of the subscription agent (e.g. "Running", "Paused", "Stopped")
    /// </summary>
    public string? AgentStatus { get; set; }

    /// <summary>
    /// If paused, the reason for the pause (typically an exception message)
    /// </summary>
    public string? PauseReason { get; set; }

    /// <summary>
    /// The node number that is currently running this shard
    /// </summary>
    public int? RunningOnNode { get; set; }

    /// <summary>
    /// Configured threshold for warning-level health check when shard is behind by this many events.
    /// Null means use default.
    /// </summary>
    public long? WarningBehindThreshold { get; set; }

    /// <summary>
    /// Configured threshold for critical-level health check when shard is behind by this many events.
    /// Null means use default.
    /// </summary>
    public long? CriticalBehindThreshold { get; set; }

    public override string ToString()
    {
        return $"{nameof(ShardName)}: {ShardName}, {nameof(Sequence)}: {Sequence}, {nameof(Action)}: {Action}";
    }

    protected bool Equals(ShardState other)
    {
        return ShardName == other.ShardName && Sequence == other.Sequence;
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

        return Equals((ShardState)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return ((ShardName != null ? ShardName.GetHashCode() : 0) * 397) ^ Sequence.GetHashCode();
        }
    }
}
