using JasperFx.Core.Reflection;

namespace JasperFx.Events.CodeGeneration;

/// <summary>
///     This would be a helper for the open ended EventProjection
/// </summary>
public class ProjectMethodCollection: MethodCollection
{
    private readonly Type _operationsType;
    public static readonly string MethodName = "Project";


    public ProjectMethodCollection(Type projectionType, Type operationsType): base(MethodName, projectionType, null)
    {
        _operationsType = operationsType;
        _validArgumentTypes.Add(operationsType);
        _validReturnTypes.Add(typeof(void));
        _validReturnTypes.Add(typeof(Task));
    }

    protected override void validateMethod(MethodSlot method)
    {
        if (method.Method.GetParameters().All(x => x.ParameterType != _operationsType))
        {
            method.AddError($"{_operationsType.FullNameInCode()} is a required parameter");
        }
    }

    public override IEventHandlingFrame CreateEventTypeHandler(Type aggregateType,
        IStorageMapping aggregateMapping,
        MethodSlot slot)
    {
        return new ProjectMethodCall(slot);
    }
}
