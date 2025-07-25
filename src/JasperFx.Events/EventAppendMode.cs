namespace JasperFx.Events;

public enum EventAppendMode
{
    /// <summary>
    /// Default behavior that ensures that all inline projections will have full access to all event
    /// metadata including intended event sequences, versions, and timestamps
    /// </summary>
    Rich,

    /// <summary>
    /// Stripped down, more performant mode of appending events that will omit some event metadata within
    /// inline projections. Event timestamps are taken from the database server
    /// </summary>
    Quick,
    
    /// <summary>
    /// Stripped down, more performant mode of appending events that will omit some event metadata within
    /// inline projections. Event timestamps are taken from TimeProvider in .NET. Use this option if you
    /// need to override event timestamps
    /// </summary>
    QuickWithServerTimestamps
}