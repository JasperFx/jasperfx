using JasperFx.Events.Daemon;

namespace JasperFx.Events.Projections;

public interface IProjectionBatch : IAsyncDisposable
{
    Task ExecuteAsync(CancellationToken token);
    
    void QuickAppendEventWithVersion(StreamAction action, IEvent @event);
    void UpdateStreamVersion(StreamAction action);
    void QuickAppendEvents(StreamAction action);
    
    // This is for publishing side effects from event slices in aggregation projections
    Task PublishMessageAsync(object message, string tenantId);

    /// <summary>
    ///     Publish a side-effect message with per-message metadata. Downstream
    ///     <see cref="IMessageSink"/> consumers use the metadata to stamp delivery
    ///     options (correlation id, causation id, headers, user name) on outgoing
    ///     envelopes. The default implementation drops the metadata and forwards
    ///     to the tenant-only overload so implementations that don't care about
    ///     metadata stay binary-compatible.
    /// </summary>
    Task PublishMessageAsync(object message, MessageMetadata metadata)
        => PublishMessageAsync(message, metadata.TenantId);

    /// <summary>
    /// Only necessary within composite projection execution
    /// </summary>
    /// <param name="range"></param>
    ValueTask RecordProgress(EventRange range);
}

public interface IProjectionBatch<TOperations, TQuerySession> : IProjectionBatch where TOperations : TQuerySession, IStorageOperations
{
    TOperations SessionForTenant(string tenantId);
}
