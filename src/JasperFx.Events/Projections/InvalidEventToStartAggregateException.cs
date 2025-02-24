using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections;

public class InvalidEventToStartAggregateException : Exception
{
    public static string ToMessage(Type aggregateType, Type projectionType, Type eventType)
    {
        var writer = new StringWriter();
        writer.WriteLine($"An aggregation projection for aggregate type {aggregateType.FullNameInCode()} cannot be created by event type {eventType.FullNameInCode()}");
        writer.WriteLine("This error usually occurs when an unexpected event type is the first event encountered for this type of projected aggregate");
        writer.WriteLine("The valid options for starting a new projected aggregate for the event type would be:");
        writer.WriteLine($"An empty, public constructor of signature new {aggregateType.ShortNameInCode()}()");
        writer.WriteLine($"A static method on {aggregateType.ShortNameInCode()} of signature public static {aggregateType.ShortNameInCode()} Create({eventType.ShortNameInCode()}) or public static {aggregateType.ShortNameInCode()} Create({typeof(IEvent<>).MakeGenericType(eventType).ShortNameInCode()})");

        if (projectionType != null)
        {
            writer.WriteLine($"{projectionType.FullNameInCode()}.Create({eventType.ShortNameInCode()})");
            writer.WriteLine($"{projectionType.FullNameInCode()}.Create({typeof(IEvent<>).MakeGenericType(eventType).ShortNameInCode()})");
        }

        // TODO -- link to documentation page explaining this better
        
        return writer.ToString();
    }
    
    public InvalidEventToStartAggregateException(Type aggregateType, Type projectionType, Type eventType) : base(ToMessage(aggregateType, projectionType, eventType))
    {
    }
}