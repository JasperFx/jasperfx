namespace JasperFx.Events;

/// <summary>
/// Metadata snapshot of an event stream.
/// </summary>
public class StreamState
{
    public StreamState()
    {
    }

    public StreamState(Guid id, long version, Type? aggregateType,
        DateTimeOffset lastTimestamp, DateTimeOffset created)
    {
        Id = id;
        Version = version;
        AggregateType = aggregateType;
        LastTimestamp = lastTimestamp;
        Created = created;
    }

    public StreamState(string key, long version, Type? aggregateType,
        DateTimeOffset lastTimestamp, DateTimeOffset created)
    {
        Key = key;
        Version = version;
        AggregateType = aggregateType;
        LastTimestamp = lastTimestamp;
        Created = created;
    }

    /// <summary>
    /// Identity of the stream when using Guid identity. <see cref="Guid.Empty"/> if the stream is string-keyed.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Identity of the stream when using string identity. Null if the stream is Guid-keyed.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Current version of the stream in the database (the count of events).
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// If the stream was started tagged with an aggregate type, that type is reflected here. May be null.
    /// </summary>
    public Type? AggregateType { get; set; }

    /// <summary>
    /// The last time this stream was appended to.
    /// </summary>
    public DateTimeOffset LastTimestamp { get; set; }

    /// <summary>
    /// The time at which this stream was created.
    /// </summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>
    /// Is this event stream marked as archived.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// The stream version through which events have been folded into a <c>Compacted&lt;T&gt;</c>
    /// snapshot — the compaction watermark. 0 means the stream has never been compacted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes an "events since last compaction" policy expressible: <see cref="Version"/>
    /// keeps growing forever, so a <c>Version &gt; N</c> threshold would re-fire on every evaluation
    /// after the first compaction, while <c>(Version - CompactedVersion) &gt; N</c> measures only the
    /// growth since the last one. A stream compacted through version 9 that has since received two
    /// appends reads <c>Version</c> 11, <c>CompactedVersion</c> 9, un-compacted growth 2. See
    /// jasperfx#740.
    /// </para>
    /// <para>
    /// Stores surface this from their streams-table metadata, both here and on the results of
    /// <see cref="IReadOnlyEventStore.QueryStreamStates"/>, where it participates in <c>Where()</c>
    /// translation like every other member.
    /// </para>
    /// </remarks>
    public long CompactedVersion { get; set; }
}
