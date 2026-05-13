using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Internals;

namespace JasperFx.Events.Aggregation;

[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
    Justification = "Class-level: scans constructors / public methods on aggregateType and projectionType to discover Create handlers. Both types are preserved at the registration boundary.")]
[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "Class-level: assigns reflective ConstructorInfo/MethodInfo results to DAM-annotated targets when building MethodSlot. Source types preserved at registration.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: Type.MakeGenericType for Task<T>.MakeGenericType(aggregateType) — runtime code generation. AOT consumers should rely on the source-generated evolver per the AOT publishing guide.")]
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
