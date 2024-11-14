namespace JasperFx.Events.CodeGeneration;

/// <summary>
///     This would be a helper for the open ended EventProjection
/// </summary>
public class CreateDocumentMethodCollection: MethodCollection
{
    private readonly Type _operationsType;
    public static readonly string MethodName = "Create";
    public static readonly string TransformMethodName = "Transform";



    public CreateDocumentMethodCollection(Type projectionType, Type operationsType): base(new[] { MethodName, TransformMethodName },
        projectionType, null)
    {
        _operationsType = operationsType;
        _validArgumentTypes.Add(operationsType);
    }

    public override IEventHandlingFrame CreateEventTypeHandler(Type aggregateType,
        IStorageMapping aggregateMapping,
        MethodSlot slot)
    {
        return new CreateMethodFrame(_operationsType, slot);
    }

    protected override void validateMethod(MethodSlot method)
    {
        if (method.ReturnType == typeof(void))
        {
            method.AddError("The return value must be a new document");
        }
    }
}