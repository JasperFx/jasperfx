namespace JasperFx.Events.CodeGeneration;

public class ShouldDeleteMethodCollection: MethodCollection
{
    public static readonly string MethodName = "ShouldDelete";

    public ShouldDeleteMethodCollection(Type projectionType, Type aggregateType, Type querySessionType): base(MethodName, projectionType,
        aggregateType)
    {
        _validArgumentTypes.Add(querySessionType);
        _validArgumentTypes.Add(aggregateType);

        _validReturnTypes.Add(typeof(bool));
    }

    public override IEventHandlingFrame CreateEventTypeHandler(Type aggregateType,
        IStorageMapping aggregateMapping, MethodSlot slot)
    {
        return new ShouldDeleteFrame(slot);
    }

    protected override void validateMethod(MethodSlot method)
    {
        if (!method.Method.GetParameters().Any())
        {
            method.AddError(
                "ShouldDelete() requires at least one argument (the event type, the aggregate type, or IQuerySession)");
        }
    }
}
