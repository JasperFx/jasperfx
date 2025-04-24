namespace JasperFx.Events;

public interface IMessageSink
{
    ValueTask PublishAsync<T>(T message, string tenantId);
}
