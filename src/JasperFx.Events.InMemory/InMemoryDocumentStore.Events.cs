using JasperFx.Events.Appending;
using JasperFx.MultiTenancy;

namespace JasperFx.Events.InMemory;

/// <summary>
/// The event half of the in-memory prototyping store (jasperfx#964): streams and events held in memory,
/// appended inside the same all-or-nothing commit as the documents.
/// </summary>
public partial class InMemoryDocumentStore
{
    // Both guarded by the commit gate, for reads as well as writes: a reader must never see half a commit.
    private readonly Dictionary<object, StreamRow> _streams = new();
    private readonly List<IEvent> _events = new();

    /// <summary>
    /// The event configuration: stream identity (<see cref="StreamIdentity.AsGuid"/> by default), event
    /// types, and which metadata is recorded on events.
    /// </summary>
    public InMemoryEventRegistry Events { get; } = new();

    private Projections.InMemoryProjectionGraph? _projections;

    /// <summary>
    /// The projections registered on the store, and the source of the aggregators live aggregation folds
    /// with (jasperfx#964).
    /// </summary>
    public Projections.InMemoryProjectionGraph Projections => _projections ??= buildProjections();

    private Projections.InMemoryProjectionGraph buildProjections()
    {
        var projections = new Projections.InMemoryProjectionGraph(Events);

        // Self-aggregating types with a source-generated evolver are reported by AllAggregateTypes() without
        // being registered, as on Marten, Polecat and Fisher.
        projections.DiscoverGeneratedEvolvers(AppDomain.CurrentDomain.GetAssemblies());

        return projections;
    }

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
    /// Append a unit of work's streams. Called from inside <see cref="CommitAtomicallyAsync"/>, so any throw
    /// here -- a collision, a stale expected version -- rolls back the documents and every stream.
    /// </summary>
    /// <remarks>
    /// On the same decision table as the stores that number events client-side (jasperfx#1051), which is what
    /// puts that table under the compliance suites in this repository. The commit gate is what makes the
    /// reads below safe to plan from.
    /// </remarks>
    internal void AppendStreams(IReadOnlyCollection<StreamAction> streams, IMetadataContext session)
    {
        // StreamAction.AddEvent stamped the stream's tenant at the time the event was added, and the plan
        // only fills an EMPTY tenant -- so the session's tenant is applied here explicitly.
        foreach (var @event in streams.SelectMany(x => x.Events))
        {
            @event.TenantId = session.TenantId;
        }

        var eventCount = _events.Count;

        var plan = ClientSideAppendPlanner.Plan(streams, rowFor, new ClientSideAppendContext
        {
            Metadata = session,
            Timestamp = Events.TimeProvider.GetUtcNow(),
            Tenancy = TenancyStyle.Single,

            // Rich whatever the registry says: this store is its own database, so nothing assigns a
            // sequence behind the plan's back for a Quick mode to read back.
            AppendMode = EventAppendMode.Rich,
            ReservedSequences = Enumerable.Range(1, streams.Sum(x => x.Events.Count))
                .Select(i => (long)eventCount + i)
                .ToArray()
        });

        plan.Apply();

        foreach (var write in plan.Writes)
        {
            var stream = write.Stream;
            var key = StreamKeyFor(stream);

            _events.AddRange(stream.Events);

            var last = stream.Events[^1].Timestamp;
            _streams[key] = _streams.TryGetValue(key, out var row)
                ? row with
                {
                    Version = stream.Version,
                    LastTimestamp = last,
                    AggregateType = row.AggregateType ?? stream.AggregateType
                }
                : new StreamRow(key, stream.Version, stream.AggregateType, stream.Events[0].Timestamp, last,
                    session.TenantId);
        }
    }

    private StreamRowSnapshot rowFor(StreamAction stream)
        => _streams.TryGetValue(StreamKeyFor(stream), out var row)
            ? StreamRowSnapshot.Live(row.Version)
            : StreamRowSnapshot.Missing;

    internal long? CurrentVersion(object streamKey)
    {
        using (EnterReadGate())
        {
            return _streams.TryGetValue(streamKey, out var row) ? row.Version : null;
        }
    }

    internal StreamState? StreamStateFor(object streamKey)
    {
        using (EnterReadGate())
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
        using (EnterReadGate())
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
        using (EnterReadGate())
        {
            return _events.FirstOrDefault(x => x.Id == eventId);
        }
    }
}
