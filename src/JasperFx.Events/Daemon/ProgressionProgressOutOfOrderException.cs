using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
/// Thrown when a projection-progression update fails an ordering / optimistic-concurrency
/// check — typically because another process advanced the progression between this node's
/// read and write.
/// </summary>
/// <remarks>
/// The original shared 1-arg <see cref="ShardName"/> ctor (used by Marten) is preserved.
/// Polecat shipped its own richer 3-arg variant
/// (<c>projectionName, expectedFloor, ceiling</c> + <see cref="ExpectedFloor"/> /
/// <see cref="AttemptedCeiling"/> props); that ctor and those props are folded in here so the
/// two stores share one type. Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public class ProgressionProgressOutOfOrderException: Exception
{
    public ProgressionProgressOutOfOrderException(ShardName progressionOrShardName): base(
        $"Progression '{progressionOrShardName}' is out of order. This may happen when multiple processes try to process the projection")
    {
    }

    public ProgressionProgressOutOfOrderException(string projectionName, long expectedFloor, long ceiling): base(
        $"Progression for '{projectionName}' is out of order. Expected floor {expectedFloor} but it had already moved. Attempted ceiling: {ceiling}.")
    {
        ProjectionName = projectionName;
        ExpectedFloor = expectedFloor;
        AttemptedCeiling = ceiling;
    }

    /// <summary>
    /// The projection whose progression was out of order. Null when constructed via the
    /// <see cref="ShardName"/> ctor.
    /// </summary>
    public string? ProjectionName { get; }

    /// <summary>
    /// The progression floor this update expected to advance from. Zero when constructed via
    /// the <see cref="ShardName"/> ctor.
    /// </summary>
    public long ExpectedFloor { get; }

    /// <summary>
    /// The ceiling this update attempted to write. Zero when constructed via the
    /// <see cref="ShardName"/> ctor.
    /// </summary>
    public long AttemptedCeiling { get; }
}
