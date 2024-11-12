using System.Reflection;
using JasperFx.CodeGeneration.Frames;

namespace JasperFx.Events.CodeGeneration;

/// <summary>
///     Calls an AggregatedProjection.Create() method
/// </summary>
public class CreateAggregateFrame: MethodCall, IEventHandlingFrame
{
    public CreateAggregateFrame(Type handlerType, string methodName, Type aggregateType): base(handlerType,
        methodName)
    {
        ReturnAction = ReturnAction.Return;
        EventType = Method.GetEventType(aggregateType);
    }

    public CreateAggregateFrame(MethodSlot slot): base(slot.HandlerType, (MethodInfo)slot.Method)
    {
        ReturnAction = ReturnAction.Return;
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

        if (parent.Aggregate != null)
        {
            AssignResultTo(parent.Aggregate);
        }
        else
        {
            ReturnAction = ReturnAction.Return;
        }
    }
}
