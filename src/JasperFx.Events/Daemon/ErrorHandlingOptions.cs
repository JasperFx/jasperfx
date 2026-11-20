namespace JasperFx.Events.Daemon;

public class ErrorHandlingOptions
{
    /// <summary>
    /// What the daemon does when user projection code fails on an event (marten#6003). The default is
    /// <see cref="ProjectionErrorAction.Pause" />. <see cref="SkipApplyErrors" /> is the older spelling of the
    /// same setting.
    /// </summary>
    /// <remarks>
    /// A systemic database failure is never skipped or parked (jasperfx#1029): a non-transient database exception
    /// raised from projection code -- a missing column, a permissions problem -- would fail every event the
    /// same way, so it pauses the shard instead. See
    /// <see cref="Projections.ProjectionExceptions.IsSystemicDatabaseFailure" />.
    /// </remarks>
    public ProjectionErrorAction ApplyErrors { get; set; } = ProjectionErrorAction.Pause;

    /// <summary>
    /// What the daemon does when the store can't deserialize an event (marten#6003). The default is
    /// <see cref="ProjectionErrorAction.Pause" />. <see cref="SkipSerializationErrors" /> is the older spelling
    /// of the same setting. <see cref="ProjectionErrorAction.Park" /> parks the event's stream for a single-stream
    /// aggregation and falls back to <see cref="ProjectionErrorAction.Pause" /> everywhere else.
    /// </summary>
    public ProjectionErrorAction SerializationErrors { get; set; } = ProjectionErrorAction.Pause;

    /// <summary>
    /// Should the daemon skip any "poison pill" events that fail in user projection code? Reading this answers
    /// whether <see cref="ApplyErrors" /> is <see cref="ProjectionErrorAction.Skip" />. Setting it to true sets
    /// <see cref="ApplyErrors" /> to <see cref="ProjectionErrorAction.Skip" />, and false sets it to
    /// <see cref="ProjectionErrorAction.Pause" />.
    /// </summary>
    /// <remarks>
    /// A systemic database failure is never skipped (jasperfx#1029): a non-transient database exception
    /// raised from projection code -- a missing column, a permissions problem -- would fail every event the
    /// same way, so it pauses the shard instead of being dead-lettered. See
    /// <see cref="Projections.ProjectionExceptions.IsSystemicDatabaseFailure" />.
    /// </remarks>
    public bool SkipApplyErrors
    {
        get => ApplyErrors == ProjectionErrorAction.Skip;
        set => ApplyErrors = value ? ProjectionErrorAction.Skip : ProjectionErrorAction.Pause;
    }

    /// <summary>
    /// Should the daemon skip any unknown event types encountered when trying to
    /// fetch events?
    /// </summary>
    public bool SkipUnknownEvents { get; set; }

    /// <summary>
    /// Should the daemon skip any events that experience serialization errors? Reading this answers whether
    /// <see cref="SerializationErrors" /> is <see cref="ProjectionErrorAction.Skip" />. Setting it to true sets
    /// <see cref="SerializationErrors" /> to <see cref="ProjectionErrorAction.Skip" />, and false sets it to
    /// <see cref="ProjectionErrorAction.Pause" />.
    /// </summary>
    public bool SkipSerializationErrors
    {
        get => SerializationErrors == ProjectionErrorAction.Skip;
        set => SerializationErrors = value ? ProjectionErrorAction.Skip : ProjectionErrorAction.Pause;
    }

    /// <summary>
    /// The most identities one shard may hold parked (marten#6003), per tenant shard under tenant-partitioned
    /// events. A batch that would park past it does not commit: the shard pauses with
    /// <see cref="ShardFailureCategory.ParkedLimitReached" /> and the existing parks stay. The default is 1,000.
    /// </summary>
    public int MaxParkedIdentitiesPerShard { get; set; } = 1000;

    /// <summary>
    /// The systemic-failure trip (marten#6003): when one batch would park more than
    /// <see cref="ParkedTripFraction" /> of the identities in it, and at least this many, the shard pauses with
    /// <see cref="ShardFailureCategory.ParkedLimitReached" /> instead of parking them. A projection bug on a new
    /// event type fails every slice that sees it, and this stops it on the first batch. The default is 20.
    /// </summary>
    public int ParkedTripMinimum { get; set; } = 20;

    /// <summary>
    /// The share of a batch's identities that has to fail before the systemic-failure trip pauses the shard. See
    /// <see cref="ParkedTripMinimum" />. The default is 0.5, "more than half".
    /// </summary>
    public double ParkedTripFraction { get; set; } = 0.5;

    /// <summary>
    /// The most parked identities a "replay all" or "replay oldest N" binds to one batch of the shard
    /// (marten#6003). The default is 10.
    /// </summary>
    public int ParkedReplayBatchSize { get; set; } = 10;
}
