using JasperFx.Events.InMemory.Projections;
using JasperFx.MultiTenancy;

namespace JasperFx.Events.InMemory;

/// <summary>
/// The stored-event rewrite (jasperfx#1215) and the projection backfill (jasperfx#1222) on the in-memory prototyping
/// store, so their compliance suites have a reference store in this repository. Each page and its cursor are
/// applied under the commit gate, which is this store's transaction.
/// </summary>
public partial class InMemoryDocumentStore
{
    /// <inheritdoc />
    public bool SupportsEventRewrites => true;

    /// <inheritdoc />
    public bool SupportsBackfills => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventRewriteProgress>> RewriteStoredEventsAsync(EventRewriteRun run,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(run);
        assertOwnDatabase(run.Database);

        var definition = run.Definition;
        var transforms = Events.FindRewriteTransforms(definition);
        var aliases = definition.EventTypes.ToHashSet(StringComparer.Ordinal);

        bool selected(IEvent e) => definition.AllEventTypes || aliases.Contains(e.EventTypeName) ||
                                   transforms.Any(x => x.AppliesTo(e.Data));

        var totals = new List<EventRewriteProgress>();
        foreach (var cursor in run.Cursors)
        {
            var total = new EventRewriteProgress(cursor.TenantId, cursor.ResumeAfter, 0, 0, 0, 0, 0, 0,
                cursor.IsComplete);
            var position = cursor.ResumeAfter;

            while (!total.Completed)
            {
                token.ThrowIfCancellationRequested();

                EventRewriteProgress page;
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var events = pageOf(cursor, position, selected, definition.BatchSize);

                    long rewritten = 0;
                    foreach (var e in events)
                    {
                        var data = e.Data;
                        foreach (var transform in transforms.Where(x => x.AppliesTo(data)))
                        {
                            data = transform.Apply(data);
                        }

                        if (Equals(data, e.Data)) continue;

                        rewritten++;
                        if (!definition.DryRun) replaceData(e, data);
                    }

                    var completed = events.Count == 0 ||
                                    !pageOf(cursor, events[^1].Sequence, selected, 1).Any();
                    position = completed ? cursor.StopAfter : events[^1].Sequence;

                    if (!definition.DryRun)
                    {
                        await run.Database.SetEventRangeMarkAsync(run.CursorKey, cursor.TenantId, position, token)
                            .ConfigureAwait(false);
                    }

                    page = new EventRewriteProgress(cursor.TenantId, position, events.Count, rewritten, 0, 0, 0, 0,
                        completed);
                }
                finally
                {
                    _gate.Release();
                }

                total = total with
                {
                    Sequence = page.Sequence,
                    Visited = total.Visited + page.Visited,
                    Rewritten = total.Rewritten + page.Rewritten,
                    Completed = page.Completed
                };

                if (run.OnPage != null) await run.OnPage(page, token).ConfigureAwait(false);

                if (!page.Completed && definition.PauseBetweenBatches > TimeSpan.Zero)
                {
                    await Task.Delay(definition.PauseBetweenBatches, token).ConfigureAwait(false);
                }
            }

            totals.Add(total);
        }

        return totals;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackfillProgress>> RunBackfillAsync(BackfillRun run, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(run);
        assertOwnDatabase(run.Database);

        var source = Projections.All.OfType<IInMemoryBackfillSource>()
                         .FirstOrDefault(x => x.Name == run.ProjectionName)
                     ?? throw new InvalidOperationException(
                         $"No single-stream projection named '{run.ProjectionName}' is registered on this store.");

        var backfill = source.Backfills.FirstOrDefault(x => x.Name == run.BackfillName)
                       ?? throw new InvalidOperationException(
                           $"Projection {run.ProjectionName} declares no backfill named '{run.BackfillName}'.");

        bool selected(IEvent e) => backfill.Handles(e.Data);

        var totals = new List<BackfillProgress>();
        foreach (var cursor in run.Cursors)
        {
            var total = new BackfillProgress(cursor.TenantId, cursor.ResumeAfter, 0, 0, cursor.IsComplete);
            var position = cursor.ResumeAfter;

            while (!total.Completed)
            {
                token.ThrowIfCancellationRequested();

                BackfillProgress page;
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var events = pageOf(cursor, position, selected, run.BatchSize);

                    long applied = 0;
                    foreach (var e in events)
                    {
                        var id = e.StreamKey is { } key && Events.StreamIdentity == StreamIdentity.AsString
                            ? (object)key
                            : e.StreamId;

                        var scope = ScopeFor(source.DocumentType, e.TenantId ?? StorageConstants.DefaultTenantId);
                        var storage = StorageFor(source.DocumentType, e.TenantId ?? StorageConstants.DefaultTenantId);
                        if (!storage.TryGetValue(id, out var document) ||
                            IsDeleted(source.DocumentType, scope, id))
                        {
                            continue;
                        }

                        backfill.Apply(document, e.Data);
                        applied++;
                    }

                    var completed = events.Count == 0 ||
                                    !pageOf(cursor, events[^1].Sequence, selected, 1).Any();
                    position = completed ? cursor.StopAfter : events[^1].Sequence;

                    await run.Database.SetEventRangeMarkAsync(run.CursorKey, cursor.TenantId, position, token)
                        .ConfigureAwait(false);

                    page = new BackfillProgress(cursor.TenantId, position, events.Count, applied, completed);
                }
                finally
                {
                    _gate.Release();
                }

                total = total with
                {
                    Sequence = page.Sequence,
                    Visited = total.Visited + page.Visited,
                    Applied = total.Applied + page.Applied,
                    Completed = page.Completed
                };

                if (run.OnPage != null) await run.OnPage(page, token).ConfigureAwait(false);
            }

            totals.Add(total);
        }

        return totals;
    }

    // Called under the commit gate
    private List<IEvent> pageOf(EventRangeCursor cursor, long after, Func<IEvent, bool> selected, int batchSize)
        => _events
            .Where(x => x.Sequence > after && x.Sequence <= cursor.StopAfter)
            .Where(x => cursor.TenantId == null || x.TenantId == cursor.TenantId)
            .Where(selected)
            .OrderBy(x => x.Sequence)
            .Take(Math.Max(batchSize, 1))
            .ToList();

    private void assertOwnDatabase(IEventDatabase database)
    {
        if (!ReferenceEquals(database, Database))
        {
            throw new ArgumentOutOfRangeException(nameof(database),
                "The run names a database that does not belong to this in-memory store.");
        }
    }

    private static void replaceData(IEvent e, object data)
    {
        if (e is not JasperFx.Events.Protected.IReplaceableEventData replaceable)
        {
            throw new InvalidOperationException($"{e.GetType().FullName} cannot have its data replaced.");
        }

        replaceable.ReplaceData(data);
    }
}
