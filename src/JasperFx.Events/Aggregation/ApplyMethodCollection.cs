using JasperFx.Core.Reflection;
using JasperFx.Events.Internals;

namespace JasperFx.Events.Aggregation;

internal class ApplyMethodCollection: MethodCollection
{
    public static readonly string MethodName = "Apply";

    public ApplyMethodCollection(Type sessionType, Type projectionType, Type aggregateType): base(MethodName, projectionType,
        aggregateType)
    {
        _validArgumentTypes.Add(sessionType);
        _validArgumentTypes.Add(aggregateType);

        _validReturnTypes.Add(typeof(Task));
        _validReturnTypes.Add(typeof(void));
        _validReturnTypes.Add(aggregateType);
        _validReturnTypes.Add(typeof(Task<>).MakeGenericType(aggregateType));
    }

    internal override void validateMethod(MethodSlot method)
    {
        if (!method.DeclaredByAggregate && method.Method.GetParameters().All(x => x.ParameterType != AggregateType))
        {
            method.AddError($"Aggregate type '{AggregateType.FullNameInCode()}' is required as a parameter");
        }

        if (_validArgumentTypes.Contains(method.EventType!))
        {
            method.DisallowEventType();
        }
    }

}
