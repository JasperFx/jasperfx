namespace JasperFx.Events.Daemon;

/// <summary>
/// Thrown when user projection or subscription code fails while applying a single event — the classic
/// "poison pill". Carries the offending event, which is what lets both the
/// <see cref="ErrorHandlingOptions.SkipApplyErrors"/> dead-letter path and (jasperfx#565) the pause
/// reporting name it.
/// </summary>
public class ApplyEventException: Exception, IEventFailureContext
{
    public ApplyEventException(IEvent @event, Exception innerException): base(
        $"Failure to apply event #{@event.Sequence} Id({@event.Id})", innerException)
    {
        Event = @event;
    }

    public IEvent Event { get; }

    ShardFailureCategory IEventFailureContext.Category => ShardFailureCategory.ApplyEvent;

    long IEventFailureContext.Sequence => Event.Sequence;

    string? IEventFailureContext.EventTypeName => Event.EventTypeName;

    Guid? IEventFailureContext.EventId => Event.Id;

    // Guid.Empty is how a string-keyed stream reports "no Guid id", and an empty key is the mirror image
    // on a Guid-keyed stream. Normalize both away so a consumer never renders a meaningless value.
    Guid? IEventFailureContext.StreamId => Event.StreamId == Guid.Empty ? null : Event.StreamId;

    string? IEventFailureContext.StreamKey => string.IsNullOrEmpty(Event.StreamKey) ? null : Event.StreamKey;

    string? IEventFailureContext.TenantId => Event.TenantId;

    long? IEventFailureContext.Version => Event.Version;
}
