namespace JasperFx.Events.Projections;

internal class ShouldDeleteMethodCollection: MethodCollection
{
    public static readonly string MethodName = "ShouldDelete";

    public ShouldDeleteMethodCollection(Type sessionType, Type projectionType, Type aggregateType): base(MethodName, projectionType,
        aggregateType)
    {
        _validArgumentTypes.Add(sessionType);
        _validArgumentTypes.Add(aggregateType);

        _validReturnTypes.Add(typeof(bool));
    }

    internal override void validateMethod(MethodSlot method)
    {
        if (!method.Method.GetParameters().Any())
        {
            method.AddError(
                "ShouldDelete() requires at least one argument (the event type, the aggregate type, or IQuerySession)");
        }
    }
}
