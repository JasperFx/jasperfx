using System.Reflection;
using JasperFx.CodeGeneration.Frames;

namespace JasperFx.Events.CodeGeneration;

internal class ProjectMethodCall: MethodCall, IEventHandlingFrame
{
    public ProjectMethodCall(MethodSlot slot): base(slot.HandlerType, (MethodInfo)slot.Method)
    {
        EventType = Method.GetEventType(null);
        Target = slot.Setter;
    }

    public Type EventType { get; }

    public void Configure(EventProcessingFrame parent)
    {
        // Replace any arguments to IEvent<T>

        TrySetArgument(parent.SpecificEvent);

        // Replace any arguments to the specific T event type
        TrySetArgument(parent.DataOnly);
    }
}
