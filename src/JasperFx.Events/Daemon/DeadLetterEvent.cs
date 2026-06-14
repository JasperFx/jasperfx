using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public class DeadLetterEvent
{
#pragma warning disable CS8618 
    public DeadLetterEvent()
#pragma warning restore CS8618 
    {
    }

    public DeadLetterEvent(IEvent e, ShardName shardName, ApplyEventException ex)
    {
        ProjectionName = shardName.Name;
        ShardName = shardName.ShardKey;
        Timestamp = DateTimeOffset.UtcNow;
        ExceptionMessage = ex.Message;

        EventSequence = e.Sequence;
        TenantId = e.TenantId;

        ExceptionType = ex.InnerException?.GetType()!.NameInCode()!;
    }

    public Guid Id { get; set; }
    public string ProjectionName { get; set; }
    public string ShardName { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string ExceptionMessage { get; set; }
    public string ExceptionType { get; set; }
    public long EventSequence { get; set; }

    /// <summary>
    /// The tenant of the failing event (jasperfx#450 / CritterWatch#381). Under
    /// <c>UseTenantPartitionedEvents</c> the same projection shard accumulates dead letters per
    /// tenant; this records which tenant each dead letter belongs to so per-tenant counts don't
    /// collide on <c>{ProjectionName}:{ShardName}</c>. A plain data column — the dead-letter table
    /// stays store-global / <c>TenancyStyle.Single</c> (it is not a tenant boundary). On a
    /// non-partitioned store this is simply the failing event's default tenant id.
    /// </summary>
    public string? TenantId { get; set; }

    public override string ToString()
    {
        return
            $"{nameof(ProjectionName)}: {ProjectionName}, {nameof(ShardName)}: {ShardName}, {nameof(EventSequence)}: {EventSequence}";
    }
}