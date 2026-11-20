using System.Runtime.ExceptionServices;
using JasperFx.Events.Projections;
using Polly;

namespace JasperFx.Events.Daemon;

/// <summary>
/// jasperfx#1256: builds a projection batch through the store's <see cref="IEventStore{TOperations,TQuerySession}.ResiliencePipeline" />,
/// so a build that fails transiently is built again, under every error policy, until the pipeline gives up. What it
/// gives up on is the real failure, which pauses the shard with <see cref="ShardFailure.IsTransient" /> true.
/// </summary>
internal static class ResilientBatchBuild
{
    /// <summary>
    /// Is this build failure worth building again? Every failure in it is transient and none is a systemic database
    /// failure. An <see cref="ApplyEventException" /> is never transient, so a failing <c>Apply()</c> anywhere in the
    /// batch leaves the decision to the error policy.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(isTransientOne);
        }

        return isTransientOne(exception);
    }

    private static bool isTransientOne(Exception exception)
        => ProjectionExceptions.IsExceptionTransient(exception) &&
           !ProjectionExceptions.IsSystemicDatabaseFailure(exception);

    public static async Task<IProjectionBatch> BuildAsync(ResiliencePipeline? pipeline, EventRange range,
        Func<CancellationToken, Task<IProjectionBatch>> build, CancellationToken token)
    {
        // A composite member writes into its parent's batch, so building it twice would write twice
        if (pipeline == null || ReferenceEquals(pipeline, ResiliencePipeline.Empty) ||
            range.BatchBehavior != BatchBehavior.Individual)
        {
            return await build(token).ConfigureAwait(false);
        }

        var attempt = new Attempt(build);
        try
        {
            return await pipeline.ExecuteAsync(static (state, ct) => state.RunAsync(ct), attempt, token)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (attempt.Offered != null && ReferenceEquals(e, attempt.Offered))
        {
            // The pipeline gave up on, or would not handle, what it was handed. The shard pauses on the whole failure.
            attempt.Failure!.Throw();
            throw;
        }
    }

    private sealed class Attempt(Func<CancellationToken, Task<IProjectionBatch>> build)
    {
        // What the last attempt handed the pipeline, and the failure it stands for
        public Exception? Offered;
        public ExceptionDispatchInfo? Failure;

        public async ValueTask<IProjectionBatch> RunAsync(CancellationToken token)
        {
            Offered = null;
            Failure = null;

            try
            {
                return await build(token).ConfigureAwait(false);
            }
            catch (Exception e) when (!token.IsCancellationRequested && IsTransient(e))
            {
                Failure = ExceptionDispatchInfo.Capture(e);

                // Several slices failing at once: the pipeline's predicate is written against the driver's exceptions,
                // not an AggregateException, so it is handed the first of them
                if (e is AggregateException aggregate)
                {
                    Offered = aggregate.InnerExceptions[0];
                    ExceptionDispatchInfo.Throw(Offered);
                }

                Offered = e;
                throw;
            }
        }
    }
}
