namespace JasperFx.Events;

public interface IMessageSink
{
    ValueTask PublishAsync<T>(T message, string tenantId);

    /// <summary>
    ///     Publish a side-effect message with per-message metadata. Implementations
    ///     consume <paramref name="metadata"/> to stamp delivery options on the
    ///     outgoing envelope (tenant, correlation id, causation id, headers, user
    ///     name). The default implementation forwards to the tenant-only overload
    ///     for implementers that don't care about metadata — so this is a purely
    ///     additive addition and does not break existing implementations.
    /// </summary>
    ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
        => PublishAsync(message, metadata.TenantId);
}
