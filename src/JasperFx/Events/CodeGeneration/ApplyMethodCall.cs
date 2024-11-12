using System.Reflection;
using JasperFx.CodeGeneration.Frames;

namespace JasperFx.Events.CodeGeneration;

public class ApplyMethodCall: MethodCall, IEventHandlingFrame
{
    public ApplyMethodCall(Type handlerType, string methodName, Type aggregateType): base(handlerType, methodName)
    {
        EventType = Method.GetEventType(aggregateType);
    }

    public ApplyMethodCall(MethodSlot slot): base(slot.HandlerType, (MethodInfo)slot.Method)
    {
        EventType = slot.EventType;
        if (slot.Setter != null)
        {
            Target = slot.Setter;
        }
    }

    public Type EventType { get; }

    public void Configure(EventProcessingFrame parent)
    {
        // Replace any arguments to IEvent<T>
        TrySetArgument(parent.SpecificEvent);

        // Replace any arguments to the specific T event type
        TrySetArgument(parent.DataOnly);

        if (ReturnType == parent.AggregateType)
        {
            AssignResultTo(parent.Aggregate);
        }
    }
}
