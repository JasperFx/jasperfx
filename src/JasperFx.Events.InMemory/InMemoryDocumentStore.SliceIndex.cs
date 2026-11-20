using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory;

/// <summary>
/// One identity's events by stream, by DCB tag, through a projection's slice index, or by scanning (marten#6003): the
/// reference implementation of the identity-source contract
/// (<see cref="IEventStore{TOperations,TQuerySession}.LoadEventsForIdentityAsync" />) and of the slice index storage, so
/// the compliance suite runs in this repository.
/// </summary>
public partial class InMemoryDocumentStore
{
    // Guarded by the commit gate like the events: written inside a commit, or under the gate taken for writing
    private readonly Dictionary<(string Projection, uint Version), HashSet<(string Tenant, object Identity, long Sequence)>>
        _sliceIndex = new();

    /// <summary>marten#6003: every identity source, the slice index included.</summary>
    public bool SupportsIdentityEventSource(IdentityEventSource source) => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<IEvent>> LoadEventsForIdentityAsync(IEventDatabase database,
        IdentityEventLoadRequest request, CancellationToken token)
        => Task.FromResult(EventsForIdentity(request));

    /// <summary>For tests: the slice index rows of one projection version, in sequence order.</summary>
    public IReadOnlyList<SliceIndexEntry> SliceIndexFor(string projectionName, uint version = 1)
    {
        using (EnterReadGate())
        {
            return _sliceIndex.TryGetValue((projectionName, version), out var rows)
                ? rows.OrderBy(x => x.Sequence).Select(x => new SliceIndexEntry(x.Tenant, x.Identity, x.Sequence))
                    .ToList()
                : [];
        }
    }

    // Inside a projection batch's commit
    internal void WriteSliceIndex(SliceIndexRows rows, InMemoryChangeSet changes)
    {
        var key = (rows.ProjectionName, rows.ProjectionVersion);
        if (!_sliceIndex.TryGetValue(key, out var index))
        {
            index = new HashSet<(string, object, long)>();
            _sliceIndex[key] = index;
        }

        var added = new List<(string, object, long)>();
        foreach (var entry in rows.Entries)
        {
            var row = (entry.TenantId, entry.Identity, entry.Sequence);
            if (index.Add(row)) added.Add(row);
        }

        changes.OnUndo(() =>
        {
            foreach (var row in added) index.Remove(row);
        });
    }

    internal Task<long?> PruneSliceIndexAsync(SliceIndexPruneRequest request, CancellationToken token)
    {
        long? highest = null;
        return CommitAtomicallyAsync(_ =>
        {
            if (!_sliceIndex.TryGetValue((request.ProjectionName, request.ProjectionVersion), out var index))
            {
                return Task.CompletedTask;
            }

            var timestamps = _events.ToDictionary(x => x.Sequence, x => x.Timestamp);
            var doomed = index
                .Where(x => request.TenantId == null || x.Tenant == request.TenantId)
                .Where(x => x.Sequence <= request.DeleteThrough)
                .Where(x => !timestamps.TryGetValue(x.Sequence, out var at) || at < request.OlderThan)
                .ToList();

            foreach (var row in doomed)
            {
                index.Remove(row);
                highest = highest.HasValue ? Math.Max(highest.Value, row.Sequence) : row.Sequence;
            }

            return Task.CompletedTask;
        }, new InMemoryChangeSet(), token).ContinueWith(_ => highest, token,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    // A rebuild writes the index again from its start
    private void removeSliceIndexFor(string projectionName, uint? version)
    {
        foreach (var key in _sliceIndex.Keys
                     .Where(x => x.Projection == projectionName && (version == null || x.Version == version))
                     .ToArray())
        {
            _sliceIndex.Remove(key);
        }
    }

    // A rewind writes the rows above the new floor again
    private void removeSliceIndexAbove(string shardIdentity, long floor)
    {
        foreach (var ((projection, version), index) in _sliceIndex)
        {
            var name = ShardName.Compose(projection, version: version);
            var matches = shardIdentity == projection || shardIdentity == name.Identity ||
                          shardIdentity.StartsWith(name.Identity + ":", StringComparison.Ordinal);

            if (matches) index.RemoveWhere(x => x.Sequence > floor);
        }
    }

    internal IReadOnlyList<IEvent> EventsForIdentity(IdentityEventLoadRequest request)
    {
        var types = request.EventTypes;

        List<IEvent> events;
        using (EnterReadGate())
        {
            IEnumerable<IEvent> candidates = _events
                .Where(x => x.Sequence > request.AfterSequence && x.Sequence <= request.Ceiling)
                .Where(x => types.Count == 0 || types.Any(t => t.IsAssignableFrom(x.EventType)))
                .Where(x => request.IncludeArchivedEvents || !x.IsArchived);

            switch (request.Source)
            {
                case IdentityEventSource.Stream:
                    var stream = unwrapStreamIdentity(request.Identity!);
                    candidates = candidates
                        .Where(x => stream is Guid id ? x.StreamId == id : x.StreamKey == (string)stream)
                        .Where(x => x.TenantId == request.TenantId);
                    break;

                case IdentityEventSource.Tag:
                    candidates = candidates
                        .Where(x => x.TenantId == request.TenantId)
                        .Where(x => x.Tags is { Count: > 0 } tags && tags.Any(tag =>
                            tag.TagType == request.TagType &&
                            Equals(ParkedIdentityText.Unwrap(tag.Value), request.IdentityValue)));
                    break;

                case IdentityEventSource.SliceIndex:
                    var sequences = _sliceIndex.TryGetValue((request.ProjectionName!, request.ProjectionVersion),
                        out var index)
                        ? index.Where(x => x.Tenant == request.TenantId && Equals(x.Identity, request.IdentityValue))
                            .Select(x => x.Sequence)
                            .ToHashSet()
                        : [];

                    // Whatever tenant the event itself is in
                    candidates = candidates.Where(x => sequences.Contains(x.Sequence));
                    break;

                case IdentityEventSource.Scan:
                    candidates = candidates.Where(x => request.TenantId == null || x.TenantId == request.TenantId);
                    break;
            }

            events = candidates.Take(Math.Max(request.Limit, 1)).ToList();
        }

        var unreadable = events.FirstOrDefault(x => IsUnreadable(x.Sequence));
        if (unreadable == null) return events;

        if (!request.SkipSerializationErrors) throw UnreadableFailure(unreadable);

        return events.Where(x => !IsUnreadable(x.Sequence)).ToList();
    }
}
