namespace JasperFx.Events.Daemon;

/// <summary>
///     One answer from <see cref="IEventDatabase.FetchFirstEventTimestampsAfterAsync" />: the timestamp of
///     the oldest event a projection or subscription shard has not processed yet, which is what turns a lag
///     counted in events into a lag measured in time (jasperfx#1150).
///     <para>
///     A backlog in events says little on a multi-tenant fleet: 40,000 events spread over 130 tenants is
///     fine, 40,000 on one tenant for an hour is not. The age of the oldest unprocessed event tells those
///     apart, and it is also the number an SLO is written against.
///     </para>
/// </summary>
/// <param name="TenantId">The tenant of the position asked about, echoed back. Null means every tenant.</param>
/// <param name="Sequence">The position asked about, echoed back: normally the shard's persisted progression.</param>
/// <param name="Timestamp">
///     The store's timestamp of the event with the lowest sequence greater than <paramref name="Sequence" />,
///     or null when there is no such event, which means the shard is caught up.
/// </param>
public record PendingEventAge(string? TenantId, long Sequence, DateTimeOffset? Timestamp)
{
    /// <summary>
    ///     True when no event lies past <see cref="Sequence" />, so there is nothing pending.
    /// </summary>
    public bool IsCaughtUp => Timestamp == null;

    /// <summary>
    ///     How old the oldest pending event is at <paramref name="now" />, or null when the shard is caught up.
    ///     Never negative: clock skew between the database and the caller would otherwise report an event as
    ///     coming from the future.
    /// </summary>
    public TimeSpan? AgeAt(DateTimeOffset now)
    {
        if (Timestamp is not { } timestamp) return null;

        var age = now - timestamp;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
