using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

/// <summary>
///     A count of stored projection/subscription dead letter events for a single shard.
///     <see cref="ProjectionName" /> aligns with <see cref="ShardName.Name" /> and
///     <see cref="ShardKey" /> aligns with <see cref="ShardName.ShardKey" />, matching how
///     <see cref="DeadLetterEvent" /> records them. Under <c>UseTenantPartitionedEvents</c> the same
///     shard accumulates dead letters per tenant, so <see cref="TenantId" /> separates the counts that
///     would otherwise collide on <c>{ProjectionName}:{ShardKey}</c>. See jasperfx#356, jasperfx#450.
/// </summary>
/// <param name="ProjectionName">The projection name (<see cref="ShardName.Name" />).</param>
/// <param name="ShardKey">The shard key within the projection (<see cref="ShardName.ShardKey" />).</param>
/// <param name="Count">The number of stored dead letter events for this shard.</param>
/// <param name="TenantId">
///     The tenant partition this count belongs to, or null for a store-global / non-partitioned count
///     (the only behavior before per-tenant partitioning). See jasperfx#450.
/// </param>
public record DeadLetterShardCount(string ProjectionName, string ShardKey, long Count, string? TenantId = null);
