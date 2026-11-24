using System.Diagnostics;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections.Composite;

public record ExecutionStage(ISubscriptionExecution[] Executions)
{
    public Task ExecuteDownstreamAsync(EventRange range) => ExecuteDownstreamAsync(range, null);

    /// <summary>
    /// jasperfx#684: run this stage over the range, skipping each kept member up to the position it kept. A member kept
    /// through the whole range does nothing, not even record progress, so its row stays where it was. A member kept
    /// through part of it gets only the events after its position, with its floor set to that position.
    /// </summary>
    /// <param name="range">The composite's range</param>
    /// <param name="keptThrough">The position each kept member skips to. Null or empty when no member is kept.</param>
    public async Task ExecuteDownstreamAsync(EventRange range, IReadOnlyDictionary<ISubscriptionExecution, long>? keptThrough)
    {
        // Let's get some parallelization!!!
        var tasks = Executions.Select(execution =>
        {
            return Task.Run(async () =>
            {
                var keptTo = keptThrough != null && keptThrough.TryGetValue(execution, out var position) ? position : 0;
                if (keptTo >= range.SequenceCeiling)
                {
                    return (execution, actions: (IReadOnlyList<ICanWrapEvent>)[], skipped: true);
                }

                var cloned = keptTo > range.SequenceFloor
                    ? new EventRange(execution.ShardName, keptTo, range.SequenceCeiling, range.Agent)
                    {
                        Events = range.Events?.Where(x => x.Sequence > keptTo).ToList()!,
                        BatchBehavior = range.BatchBehavior,
                        ActiveBatch = range.ActiveBatch,
                        Upstream = range.Upstream
                    }
                    : range.CloneForExecutionLeaf(execution.ShardName);
                cloned.BatchBehavior = BatchBehavior.Composite;

                // #4751 guard: a composite member MUST record its progress and accumulate its read-model
                // writes into the SAME ProjectionBatch as the parent composite, so that the member's
                // progression and its writes commit atomically together with the composite shard's own
                // progression in the single batch.ExecuteAsync(). If a future change ever gave a member its
                // own batch, the composite shard's progression could advance ahead of the member read
                // models — which is exactly the premature "non-stale" hazard behind #4751. This is an
                // invariant assertion, not a behavior change: CloneForExecutionLeaf already shares the
                // parent's ActiveBatch instance.
                if (!ReferenceEquals(cloned.ActiveBatch, range.ActiveBatch))
                {
                    throw new InvalidOperationException(
                        $"Composite member '{execution.ShardName.Identity}' is not writing into the shared composite batch; " +
                        "its progression could commit ahead of its read-model writes (#4751).");
                }

                // Need to record the individual progress even though it's locked together
                await cloned.ActiveBatch!.RecordProgress(cloned);

                await execution.ProcessRangeAsync(cloned);

                return (execution, actions: cloned.AllRecordedActions(), skipped: false);
            });
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        // Propagate aggregate cache data to downstream aggregations.
        // This must happen sequentially after Task.WhenAll to avoid
        // a race condition on the shared Upstream list (GH-4151).
        foreach (var (execution, _, skipped) in results)
        {
            if (!skipped) range.Upstream.Add(execution);
        }

        // This propagates changes from upstream to downstream stages
        range.Events.InsertRange(0, results.SelectMany(x => x.actions.Select(o => o.ToEvent())));
    }
}