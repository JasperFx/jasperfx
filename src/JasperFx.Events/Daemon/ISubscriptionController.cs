using JasperFx.Events.Projections;

namespace JasperFx.Events.Daemon;

public interface ISubscriptionController
{
    ShardExecutionMode Mode { get; }

    /// <summary>
    ///     The current error handling configuration for this projection or subscription
    /// </summary>
    ErrorHandlingOptions ErrorOptions { get; }

    ShardName Name { get; }
    AsyncOptions Options { get; }

    ValueTask MarkSuccessAsync(long processedCeiling);

    /// <summary>
    ///     Tell the governing subscription agent that there was a critical error that
    ///     should pause the subscription or projection
    /// </summary>
    /// <param name="ex"></param>
    /// <returns></returns>
    Task ReportCriticalFailureAsync(Exception ex);

    /// <summary>
    ///     Tell the governing subscription agent that there was a critical error that
    ///     should pause the subscription or projection
    /// </summary>
    /// <param name="ex"></param>
    /// <param name="lastProcessed">This allows a subscription to stop at a point within a batch of events</param>
    /// <returns></returns>
    Task ReportCriticalFailureAsync(Exception ex, long lastProcessed);

    /// <summary>
    ///     Record a dead letter event for the failure to process the current event
    /// </summary>
    /// <param name="event"></param>
    /// <param name="ex"></param>
    /// <returns></returns>
    Task RecordDeadLetterEventAsync(IEvent @event, Exception ex);
}