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
        // jasperfx#565: assign the identity here rather than leaving it to the store's document identity
        // generation, so the id of a dead letter is known to the process that created it BEFORE the
        // (background, retried) write lands. Stores only generate an id when the value is empty, so
        // pre-assigning changes nothing about how the row is persisted. Version 7 keeps the ids
        // time-ordered, which is what the store's index would have wanted anyway.
        Id = Guid.CreateVersion7();

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

    /// <summary>
    /// jasperfx#565: does this dead letter describe the same failing event as <paramref name="failure" />
    /// on the shard named by <paramref name="shardName" />?
    ///
    /// <para>
    /// This is the traceability link between the two halves of a per-event failure, which are recorded on
    /// different paths and never at the same time. A shard that PAUSES (the error options do not skip)
    /// reports a <see cref="ShardFailure" /> and writes nothing here — the event was not skipped, so
    /// inflating the dead-letter counts stores use as their "projection is unhealthy" signal would be a
    /// lie, and a restart loop would rewrite the row on every attempt. A shard that SKIPS
    /// (<see cref="ErrorHandlingOptions.SkipApplyErrors" /> and friends) writes a dead letter and keeps
    /// running. Same event, same projection, same shard, same sequence, same tenant — so an operator (or
    /// CritterWatch) that has one can find the other, whether the deployment flipped the skip flag after
    /// the pause or the other way round.
    /// </para>
    /// </summary>
    public bool DescribesSameFailureAs(ShardName shardName, ShardFailure failure)
    {
        if (failure.Event == null) return false;

        return ProjectionName == shardName.Name
               && ShardName == shardName.ShardKey
               && EventSequence == failure.Event.Sequence
               // A failure detected before the event materialized may not know its tenant; don't let that
               // veto a match the sequence already established.
               && (failure.Event.TenantId == null || TenantId == null || TenantId == failure.Event.TenantId);
    }

    public override string ToString()
    {
        return
            $"{nameof(ProjectionName)}: {ProjectionName}, {nameof(ShardName)}: {ShardName}, {nameof(EventSequence)}: {EventSequence}";
    }
}