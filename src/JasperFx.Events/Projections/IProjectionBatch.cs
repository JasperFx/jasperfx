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

    /// <summary>
    /// marten#6003: write (insert or replace) the <see cref="ParkedIdentity" /> row for an identity that failed in
    /// this batch, so it commits with the batch's progression or not at all. Only called for a store that answers
    /// <see cref="IEventStore{TOperations,TQuerySession}.SupportsParkedIdentities" /> with true.
    /// </summary>
    void ParkIdentity(ParkedIdentity parked)
        => throw new NotSupportedException($"{GetType().FullName} does not park projection identities (marten#6003).");

    /// <summary>
    /// marten#6003: delete the <see cref="ParkedIdentity" /> row with this key in this batch, so the unpark commits
    /// with the re-folded document and the batch's progression. Deleting a row that isn't there is not an error.
    /// </summary>
    void UnparkIdentity(ParkedIdentityKey key)
        => throw new NotSupportedException($"{GetType().FullName} does not park projection identities (marten#6003).");

    /// <summary>
    /// marten#6003: write slice index rows in this batch, so they commit with the documents and the progression of the
    /// events they describe. Only called for a projection with <see cref="AsyncOptions.UseSliceIndex()" /> on a store
    /// that answers <see cref="IEventStore{TOperations,TQuerySession}.SupportsIdentityEventSource" /> with true for
    /// <see cref="IdentityEventSource.SliceIndex" />. May be called more than once per batch.
    /// </summary>
    void AppendSliceIndex(SliceIndexRows rows)
        => throw new NotSupportedException($"{GetType().FullName} does not write projection slice indexes (marten#6003).");
}

public interface IProjectionBatch<TOperations, TQuerySession> : IProjectionBatch where TOperations : TQuerySession, IStorageOperations
{
    TOperations SessionForTenant(string tenantId);
}
