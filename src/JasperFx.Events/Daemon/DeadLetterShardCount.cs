using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     A count of stored projection/subscription dead letter events for a single shard.
///     <see cref="ProjectionName" /> aligns with <see cref="ShardName.Name" /> and
///     <see cref="ShardKey" /> aligns with <see cref="ShardName.ShardKey" />, matching how
///     <see cref="DeadLetterEvent" /> records them. See jasperfx#356.
/// </summary>
/// <param name="ProjectionName">The projection name (<see cref="ShardName.Name" />).</param>
/// <param name="ShardKey">The shard key within the projection (<see cref="ShardName.ShardKey" />).</param>
/// <param name="Count">The number of stored dead letter events for this shard.</param>
public record DeadLetterShardCount(string ProjectionName, string ShardKey, long Count);
