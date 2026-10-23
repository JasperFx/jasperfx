namespace JasperFx.Events.Daemon;

public class ErrorHandlingOptions
{
    /// <summary>
    /// Should the daemon skip any "poison pill" events that fail in user projection code?
    /// </summary>
    /// <remarks>
    /// A systemic database failure is never skipped (jasperfx#1029): a non-transient database exception
    /// raised from projection code -- a missing column, a permissions problem -- would fail every event the
    /// same way, so it pauses the shard instead of being dead-lettered. See
    /// <see cref="Projections.ProjectionExceptions.IsSystemicDatabaseFailure" />.
    /// </remarks>
    public bool SkipApplyErrors { get; set; }

    /// <summary>
    /// Should the daemon skip any unknown event types encountered when trying to
    /// fetch events?
    /// </summary>
    public bool SkipUnknownEvents { get; set; }

    /// <summary>
    /// Should the daemon skip any events that experience serialization errors?
    /// </summary>
    public bool SkipSerializationErrors { get; set; }
}
