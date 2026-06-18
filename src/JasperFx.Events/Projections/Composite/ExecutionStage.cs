using System.Diagnostics;
using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections.Composite;

public record ExecutionStage(ISubscriptionExecution[] Executions)
{
    public async Task ExecuteDownstreamAsync(EventRange range)
    {
        // Let's get some parallelization!!!
        var tasks = Executions.Select(execution =>
        {
            return Task.Run(async () =>
            {
                var cloned = range.CloneForExecutionLeaf(execution.ShardName);
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

                return (execution, actions: cloned.AllRecordedActions());
            });
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        // Propagate aggregate cache data to downstream aggregations.
        // This must happen sequentially after Task.WhenAll to avoid
        // a race condition on the shared Upstream list (GH-4151).
        foreach (var (execution, _) in results)
        {
            range.Upstream.Add(execution);
        }

        // This propagates changes from upstream to downstream stages
        range.Events.InsertRange(0, results.SelectMany(x => x.actions.Select(o => o.ToEvent())));
    }
}