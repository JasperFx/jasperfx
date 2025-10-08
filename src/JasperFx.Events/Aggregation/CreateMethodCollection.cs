using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Internals;

namespace JasperFx.Events.Aggregation;

internal class CreateMethodCollection: MethodCollection
{
    public static readonly string MethodName = "Create";

    public CreateMethodCollection(Type sessionType, Type projectionType, Type aggregateType): base(MethodName, projectionType,
        aggregateType)
    {
        _validArgumentTypes.Add(sessionType);

        _validReturnTypes.Fill(aggregateType);
        _validReturnTypes.Add(typeof(Task<>).MakeGenericType(aggregateType));

        var constructors = aggregateType
            .GetConstructors()
            .Where(x => x.GetParameters().Length == 1 && (!x.GetParameters().Single().ParameterType.IsSimple() || x.GetParameters().Single().ParameterType.Closes(typeof(IEvent<>))));

        foreach (var constructor in constructors)
        {
            var slot = new MethodSlot(constructor, projectionType, aggregateType);
            if (ValidArgumentTypes.Contains(slot.EventType))
            {
                slot.DisallowEventType();
            }
            
            Methods.Add(slot);
        }
    }

    internal override void validateMethod(MethodSlot method)
    {
        // Nothing, no special rules
    }

    protected override BindingFlags flags()
    {
        return BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    }
}
