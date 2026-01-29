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
                
                // This allows us to propagate the aggregate cache data to
                // downstream aggregations
                range.Upstream.Add(execution);

                return cloned.AllRecordedActions();
            });
        }).ToArray();

        var updates = await Task.WhenAll(tasks);

        if (updates.SelectMany(x => x).OfType<ProjectionDeleted>().Any())
        {
            Debug.WriteLine("Okay, should have gotten here.");
        }

        // This propagates changes from upstream to downstream stages
        range.Events.InsertRange(0, updates.SelectMany(x => x.Select(o => o.ToEvent())));
    }
}