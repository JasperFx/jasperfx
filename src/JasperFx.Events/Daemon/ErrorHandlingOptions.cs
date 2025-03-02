namespace JasperFx.Events.Daemon;

public class ErrorHandlingOptions
{
    /// <summary>
    /// Should the daemon skip any "poison pill" events that fail in user projection code?
    /// </summary>
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
