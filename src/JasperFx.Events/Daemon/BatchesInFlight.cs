using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.Daemon;

internal static class BatchesInFlight
{
    /// <summary>
    /// Take a slot of <see cref="DaemonSettings.MaxConcurrentBatchesInFlightPerNode"/> for a range that is
    /// about to build its own batch. Returns the gate to release once that batch has been disposed, or null
    /// when there is nothing to release: no bound is configured, or the range is a composite member that
    /// writes into its parent's batch (the parent holds the slot, so taking another could deadlock).
    /// Must be called before the per-database <see cref="ISubscriptionAgent.BatchWriteThrottle"/> is taken.
    /// </summary>
    public static async ValueTask<SemaphoreSlim?> AcquireAsync(EventRange range, ILogger logger,
        CancellationToken token)
    {
        if (range.BatchBehavior != BatchBehavior.Individual)
        {
            return null;
        }

        var gate = range.Agent?.BatchesInFlightThrottle;
        if (gate == null)
        {
            return null;
        }

        if (!gate.Wait(0))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Shard {Name} is waiting for one of the node's projection batch slots before building {Range}",
                    range.ShardName.Identity, range);
            }

            await gate.WaitAsync(token).ConfigureAwait(false);
        }

        return gate;
    }
}
