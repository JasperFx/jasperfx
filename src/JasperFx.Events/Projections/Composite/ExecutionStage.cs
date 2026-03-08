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