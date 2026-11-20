using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory;

/// <summary>
/// Parked identities and dead letters on the in-memory prototyping store (marten#6003): the reference
/// implementation of the storage contract the daemon's parking needs, so the compliance suite runs in this
/// repository.
/// </summary>
public partial class InMemoryDocumentStore
{
    // Guarded by the commit gate like the progression: written inside a commit, or under the gate taken for writing
    private readonly Dictionary<ParkedIdentityKey, ParkedIdentity> _parked = new();
    private readonly List<DeadLetterEvent> _deadLetters = new();
    private readonly HashSet<long> _unreadable = new();

    /// <summary>marten#6003: the store implements the parked-identity seams.</summary>
    public bool SupportsParkedIdentities => true;

    /// <summary>
    /// jasperfx#1258: for tests, the post-commit work of an async projection batch, given the parked rows the batch
    /// committed. It runs where Marten's and Polecat's after-commit hooks run, after the commit and before the daemon
    /// takes the batch's parks into the shard, so a test can hold the daemon in that window.
    /// </summary>
    internal Func<IReadOnlyList<ParkedIdentity>, Task>? ProjectionBatchCommitted { get; set; }

    /// <summary>
    /// For testing how the daemon handles an event the store can't deserialize: from now on the daemon's event loader,
    /// and the per-identity rebuild's loader, treat the event with this sequence as unreadable.
    /// </summary>
    public void MarkEventUnreadable(long sequence)
    {
        using (EnterReadGate())
        {
            _unreadable.Add(sequence);
        }
    }

    /// <summary>Undo <see cref="MarkEventUnreadable" /> for every event.</summary>
    public void ClearUnreadableEvents()
    {
        using (EnterReadGate())
        {
            _unreadable.Clear();
        }
    }

    internal bool IsUnreadable(long sequence) => _unreadable.Count > 0 && _unreadable.Contains(sequence);

    internal static EventDeserializationFailureException UnreadableFailure(IEvent @event)
        => new(@event.Sequence, @event.EventTypeName,
            new InvalidOperationException($"Event #{@event.Sequence} was marked unreadable"))
        {
            StreamId = @event.StreamId == Guid.Empty ? null : @event.StreamId,
            StreamKey = @event.StreamKey,
            TenantId = @event.TenantId
        };

    // Inside a projection batch's commit
    internal void WriteParked(ParkedIdentity parked, InMemoryChangeSet changes)
    {
        var key = parked.Key;
        var existed = _parked.TryGetValue(key, out var previous);
        _parked[key] = parked;

        changes.OnUndo(() =>
        {
            if (existed) _parked[key] = previous!;
            else _parked.Remove(key);
        });
    }

    // Inside a projection batch's commit
    internal void DeleteParked(ParkedIdentityKey key, InMemoryChangeSet changes)
    {
        if (!_parked.Remove(key, out var previous)) return;

        changes.OnUndo(() => _parked[key] = previous);
    }

    internal IReadOnlyList<ParkedIdentity> ParkedRows(Func<ParkedIdentity, bool> filter)
    {
        using (EnterReadGate())
        {
            return _parked.Values.Where(filter)
                .OrderBy(x => x.ParkedAt)
                .ThenBy(x => x.FirstFailingSequence)
                .ToList();
        }
    }

    internal Task<int> RequestParkedActionAsync(ParkedActionRequest request, CancellationToken token)
    {
        var marked = 0;
        return CommitAtomicallyAsync(changes =>
        {
            var matching = _parked.Values
                .Where(x => x.ProjectionName == request.ProjectionName)
                .Where(x => request.ProjectionVersion == null || x.ProjectionVersion == request.ProjectionVersion)
                .Where(x => request.TenantId == null || x.TenantId == request.TenantId)
                .Where(x => request.IdentityText == null || x.IdentityText == request.IdentityText)
                .OrderBy(x => x.ParkedAt)
                .ThenBy(x => x.FirstFailingSequence)
                .ToList();

            if (request.IdentityText == null && request.Oldest is { } oldest)
            {
                matching = matching.Take(oldest).ToList();
            }

            var now = Events.TimeProvider.GetUtcNow();
            foreach (var row in matching)
            {
                WriteParked(row with { RequestedAction = request.Action, RequestedAt = now }, changes);
            }

            marked = matching.Count;
            return Task.CompletedTask;
        }, new InMemoryChangeSet(), token).ContinueWith(_ => marked, token, TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    internal Task RecordParkedReplayFailureAsync(ParkedIdentityKey key, DateTimeOffset attemptedAt, string error,
        CancellationToken token)
        => CommitAtomicallyAsync(changes =>
        {
            if (_parked.TryGetValue(key, out var row))
            {
                WriteParked(row with
                {
                    ReplayAttempts = row.ReplayAttempts + 1,
                    LastAttemptAt = attemptedAt,
                    LastAttemptError = error,
                    RequestedAction = null,
                    RequestedAt = null
                }, changes);
            }

            return Task.CompletedTask;
        }, new InMemoryChangeSet(), token);

    // A rebuild re-folds every identity, so it clears the projection's parked rows, every version of them
    private void removeParkedRowsFor(string projectionName)
    {
        foreach (var key in _parked.Keys.Where(x => x.ProjectionName == projectionName).ToArray())
        {
            _parked.Remove(key);
        }
    }

    // A rewind clears the parks at or above the new floor. The ones below stay: those identities are still frozen.
    private void removeParkedRowsAtOrAbove(string shardIdentity, long floor)
    {
        foreach (var (key, row) in _parked.ToArray())
        {
            var name = ShardName.Compose(row.ProjectionName, row.ShardKey, version: row.ProjectionVersion);
            var matches = shardIdentity == row.ProjectionName || shardIdentity == name.Identity ||
                          shardIdentity.StartsWith(name.Identity + ":", StringComparison.Ordinal);

            if (matches && row.FirstFailingSequence > floor) _parked.Remove(key);
        }
    }

    internal void StoreDeadLetter(DeadLetterEvent deadLetter)
    {
        using (EnterReadGate())
        {
            _deadLetters.Add(deadLetter);
        }
    }

    internal IReadOnlyList<DeadLetterEvent> DeadLetters(ShardName shard, string? tenantId)
    {
        using (EnterReadGate())
        {
            return _deadLetters
                .Where(x => x.ProjectionName == shard.Name && x.ShardName == shard.ShardKey)
                .Where(x => tenantId == null || x.TenantId == tenantId)
                .ToList();
        }
    }
}
