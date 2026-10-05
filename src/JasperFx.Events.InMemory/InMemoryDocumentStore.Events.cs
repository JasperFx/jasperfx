namespace JasperFx.Events.InMemory;

/// <summary>
/// The event half of the in-memory prototyping store (jasperfx#964): streams and events held in memory,
/// appended inside the same all-or-nothing commit as the documents.
/// </summary>
public partial class InMemoryDocumentStore
{
    // Both guarded by _commitLock, for reads as well as writes: a reader must never see half a commit.
    private readonly Dictionary<object, StreamRow> _streams = new();
    private readonly List<IEvent> _events = new();

    /// <summary>
    /// The event configuration: stream identity (<see cref="StreamIdentity.AsGuid"/> by default), event
    /// types, and which metadata is recorded on events.
    /// </summary>
    public InMemoryEventRegistry Events { get; } = new();

    /// <summary>One stream. Immutable, so a shallow copy of <see cref="_streams"/> is a full snapshot.</summary>
    internal sealed record StreamRow(
        object Id,
        long Version,
        Type? AggregateType,
        DateTimeOffset Created,
        DateTimeOffset LastTimestamp,
        string TenantId);

    internal object StreamKeyFor(StreamAction stream)
        => Events.StreamIdentity == StreamIdentity.AsGuid ? stream.Id : stream.Key!;

    /// <summary>
    /// Append a unit of work's streams. Called from inside <see cref="CommitAtomically"/>, so any throw
    /// here -- a collision, a stale expected version -- rolls back the documents and every stream.
    /// </summary>
    internal void AppendStreams(IReadOnlyCollection<StreamAction> streams, IMetadataContext session)
    {
        foreach (var stream in streams)
        {
            var key = StreamKeyFor(stream);
            _streams.TryGetValue(key, out var row);
            var currentVersion = row?.Version ?? 0;

            if (stream.Events.Count == 0)
            {
                // A stream fetched for writing with nothing appended still guards its version when it
                // asked to (FetchForWriting with AlwaysEnforceConsistency).
                if (stream is { AlwaysEnforceConsistency: true, ExpectedVersionOnServer: { } expected }
                    && expected != currentVersion)
                {
                    throw new EventStreamUnexpectedMaxEventIdException(key, stream.AggregateType, expected,
                        currentVersion);
                }

                continue;
            }

            if (stream.ActionType == StreamActionType.Start && row is not null)
            {
                throw new ExistingStreamIdCollisionException(key, stream.AggregateType);
            }

            // StreamAction.AddEvent stamped the stream's tenant at the time the event was added, and Rich
            // metadata only fills an EMPTY tenant -- so the session's tenant is applied here explicitly.
            foreach (var @event in stream.Events)
            {
                @event.TenantId = session.TenantId;
            }

            var sequences = new Queue<long>();
            for (var i = 1; i <= stream.Events.Count; i++) sequences.Enqueue(_events.Count + i);

            // Versions, sequences, ids, timestamps and metadata, plus the optimistic concurrency guard:
            // a stale expected version throws EventStreamUnexpectedMaxEventIdException.
            stream.PrepareEvents(currentVersion, Events, sequences, session);

            _events.AddRange(stream.Events);

            var last = stream.Events[^1].Timestamp;
            _streams[key] = row is null
                ? new StreamRow(key, stream.Version, stream.AggregateType, stream.Events[0].Timestamp, last,
                    session.TenantId)
                : row with
                {
                    Version = stream.Version,
                    LastTimestamp = last,
                    AggregateType = row.AggregateType ?? stream.AggregateType
                };
        }
    }

    internal long? CurrentVersion(object streamKey)
    {
        lock (_commitLock)
        {
            return _streams.TryGetValue(streamKey, out var row) ? row.Version : null;
        }
    }

    internal StreamState? StreamStateFor(object streamKey)
    {
        lock (_commitLock)
        {
            if (!_streams.TryGetValue(streamKey, out var row)) return null;

            return streamKey is Guid id
                ? new StreamState(id, row.Version, row.AggregateType, row.LastTimestamp, row.Created)
                : new StreamState((string)streamKey, row.Version, row.AggregateType, row.LastTimestamp, row.Created);
        }
    }

    internal IReadOnlyList<IEvent> EventsFor(object streamKey, long version, DateTimeOffset? timestamp,
        long fromVersion)
    {
        lock (_commitLock)
        {
            return _events
                .Where(x => streamKey is Guid id ? x.StreamId == id : x.StreamKey == (string)streamKey)
                .Where(x => version <= 0 || x.Version <= version)
                .Where(x => fromVersion <= 0 || x.Version >= fromVersion)
                .Where(x => timestamp is null || x.Timestamp <= timestamp.Value)
                .OrderBy(x => x.Version)
                .ToList();
        }
    }

    internal IEvent? EventById(Guid eventId)
    {
        lock (_commitLock)
        {
            return _events.FirstOrDefault(x => x.Id == eventId);
        }
    }
}
