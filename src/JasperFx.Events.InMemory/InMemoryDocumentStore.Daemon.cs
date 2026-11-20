using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory;

/// <summary>
/// The async daemon's storage on the in-memory prototyping store (jasperfx#1201): projection progression,
/// shard event paging and the per-identity rebuild reads.
/// </summary>
public partial class InMemoryDocumentStore
{
    // Guarded by the commit gate like the events: written inside a commit, or under the gate taken for writing
    private readonly Dictionary<string, (long Sequence, DateTimeOffset LastUpdated)> _progression = new();

    internal InMemoryDocumentSession ProjectionBatchSession(string tenantId)
        => new(this, tenantId) { QueuesProjectionWrites = true };

    internal long ProgressionFor(string shardIdentity)
    {
        using (EnterReadGate())
        {
            return _progression.TryGetValue(shardIdentity, out var row) ? row.Sequence : 0;
        }
    }

    internal IReadOnlyList<ShardState> AllProgression()
    {
        using (EnterReadGate())
        {
            return _progression
                .Select(x => new ShardState(x.Key, x.Value.Sequence) { LastUpdated = x.Value.LastUpdated })
                .ToList();
        }
    }

    internal void MarkHighWater(long sequence, DateTimeOffset now)
    {
        using (EnterReadGate())
        {
            _progression[ShardState.HighWaterMark] = (sequence, now);
        }
    }

    /// <summary>
    /// Move a shard from <paramref name="floor" /> to <paramref name="ceiling" /> inside a commit, refusing when the
    /// row no longer holds the floor -- another runner moved it, or an operator rewound it.
    /// </summary>
    internal void AdvanceProgression(ShardName shard, long floor, long ceiling, InMemoryChangeSet changes)
    {
        var key = shard.Identity;
        var existed = _progression.TryGetValue(key, out var previous);
        var current = existed ? previous.Sequence : 0;

        if (current != floor)
        {
            throw new ProgressionProgressOutOfOrderException(shard);
        }

        _progression[key] = (ceiling, Events.TimeProvider.GetUtcNow());

        changes.OnUndo(() =>
        {
            if (existed)
            {
                _progression[key] = previous;
            }
            else
            {
                _progression.Remove(key);
            }
        });
    }

    internal Task SetProgressionAsync(string shardIdentity, long sequence, CancellationToken token)
        => CommitAtomicallyAsync(changes =>
        {
            var existed = _progression.TryGetValue(shardIdentity, out var previous);
            _progression[shardIdentity] = (sequence, Events.TimeProvider.GetUtcNow());
            changes.OnUndo(() =>
            {
                if (existed) _progression[shardIdentity] = previous;
                else _progression.Remove(shardIdentity);
            });

            // marten#6003: an identity parked above the new floor is parked again when the shard gets there, and the
            // slice index rows above it are written again
            removeParkedRowsAtOrAbove(shardIdentity, sequence);
            removeSliceIndexAbove(shardIdentity, sequence);

            return Task.CompletedTask;
        }, new InMemoryChangeSet(), token);

    internal Task DeleteProgressionAsync(string subscriptionName, CancellationToken token)
        => CommitAtomicallyAsync(_ =>
        {
            removeProgressionRowsFor(subscriptionName);
            return Task.CompletedTask;
        }, new InMemoryChangeSet(), token);

    // A rebuild starts from no progression and none of the projection's documents
    internal Task TeardownProjectionAsync(string projectionName, CancellationToken token)
    {
        var found = Projections.TryFindProjection(projectionName, out var source);
        var published = found ? source!.PublishedTypes().ToArray() : [];
        uint? version = found ? source!.Version : null;

        return CommitAtomicallyAsync(_ =>
        {
            removeProgressionRowsFor(projectionName);

            // marten#6003: a rebuild re-folds every identity, so nothing stays parked, and it writes the slice index
            // again from its start
            removeParkedRowsFor(projectionName);
            removeSliceIndexFor(projectionName, version);

            foreach (var type in published)
            {
                var root = RootOf(type);
                foreach (var key in _documents.Keys.Where(x => x.Type == root).ToArray())
                {
                    _documents.TryRemove(key, out var _);
                }

                foreach (var key in _metadata.Keys.Where(x => x.Root == root).ToArray())
                {
                    _metadata.TryRemove(key, out var _);
                }
            }

            return Task.CompletedTask;
        }, new InMemoryChangeSet(), token);
    }

    private void removeProgressionRowsFor(string name)
    {
        foreach (var key in _progression.Keys.Where(x => x == name || x.StartsWith(name + ":", StringComparison.Ordinal))
                     .ToArray())
        {
            _progression.Remove(key);
        }
    }

    internal IReadOnlyList<IEvent> EventsForShard(long floor, long ceiling, int limit, EventFilterable filtering,
        string? tenantId)
    {
        var types = filtering.IncludedEventTypes;

        using (EnterReadGate())
        {
            return _events
                .Where(x => x.Sequence > floor && x.Sequence <= ceiling)
                .Where(x => types.Count == 0 || types.Any(t => t.IsAssignableFrom(x.EventType)))
                .Where(x => filtering.IncludeArchivedEvents || !x.IsArchived)
                .Where(x => tenantId == null || x.TenantId == tenantId)
                .Take(Math.Max(limit, 1))
                .ToList();
        }
    }

    // marten#6003: the shard's loader, with events marked unreadable reported, skipped or thrown the way a real
    // store's loader treats a body it can't deserialize
    internal EventPage LoadPage(EventRequest request, EventFilterable filtering)
    {
        var page = new EventPage(request.Floor);
        var loaded = EventsForShard(request.Floor, request.HighWater, request.BatchSize, filtering,
            request.Name.TenantId);

        var skipped = 0;
        foreach (var @event in loaded)
        {
            if (!IsUnreadable(@event.Sequence))
            {
                page.Add(@event);
                continue;
            }

            var failure = UnreadableFailure(@event);
            if (request.ParksUnreadableEvents)
            {
                page.AddUnreadable(new UnreadableEvent(@event.Sequence,
                    @event.StreamId == Guid.Empty ? null : @event.StreamId, @event.StreamKey, @event.TenantId,
                    @event.EventTypeName, failure));
            }
            else if (request.ErrorOptions?.SkipSerializationErrors == true)
            {
                StoreDeadLetter(failure.ToDeadLetterEvent(request.Name));
            }
            else
            {
                throw failure;
            }

            skipped++;
            page.LastObservedSequence = @event.Sequence;
        }

        if (loaded.Count > 0) page.LastObservedSequence = loaded[^1].Sequence;
        page.CalculateCeiling(request.BatchSize, request.HighWater, skipped);

        return page;
    }

    // jasperfx#1201: the same rules as the shard's loader, for one stream
    internal IReadOnlyList<IEvent> EventsForIdentityRebuild(IdentityRebuildEventRequest request)
    {
        var key = unwrapStreamIdentity(request.StreamIdentity);
        var types = request.EventTypes;

        List<IEvent> events;
        using (EnterReadGate())
        {
            events = _events
                .Where(x => x.Sequence > request.AfterSequence && x.Sequence <= request.Ceiling)
                .Where(x => key is Guid id ? x.StreamId == id : x.StreamKey == (string)key)
                .Where(x => types.Count == 0 || types.Any(t => t.IsAssignableFrom(x.EventType)))
                .Where(x => request.IncludeArchivedEvents || !x.IsArchived)
                .Where(x => x.TenantId == request.TenantId)
                .Take(Math.Max(request.Limit, 1))
                .ToList();
        }

        // marten#6003: an unreadable event fails the re-fold unless the request skips it
        var unreadable = events.FirstOrDefault(x => IsUnreadable(x.Sequence));
        if (unreadable == null) return events;

        if (!request.SkipSerializationErrors) throw UnreadableFailure(unreadable);

        return events.Where(x => !IsUnreadable(x.Sequence)).ToList();
    }

    // A strong-typed identifier wraps the stream's Guid or string
    private static object unwrapStreamIdentity(object identity)
    {
        if (identity is Guid or string) return identity;

        var inner = identity.GetType().GetProperties()
            .FirstOrDefault(x => x.PropertyType == typeof(Guid) || x.PropertyType == typeof(string));

        return inner?.GetValue(identity) ?? throw new ArgumentOutOfRangeException(nameof(identity),
            $"{identity.GetType().FullNameInCode()} is not a stream identity the in-memory store can read.");
    }
}
