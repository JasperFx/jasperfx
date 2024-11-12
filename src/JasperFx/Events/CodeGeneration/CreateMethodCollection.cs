using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;

namespace JasperFx.Events.CodeGeneration;

public class CreateMethodCollection: MethodCollection
{
    public Type FullSessionType { get; }
    public static readonly string MethodName = "Create";

    public CreateMethodCollection(Type projectionType, Type aggregateType, Type querySessionType, Type fullSessionType): base(MethodName, projectionType,
        aggregateType)
    {
        FullSessionType = fullSessionType;
        QuerySessionType = querySessionType;
        _validArgumentTypes.Add(querySessionType);

        _validReturnTypes.Fill(aggregateType);
        _validReturnTypes.Add(typeof(Task<>).MakeGenericType(aggregateType));

        var constructors = aggregateType
            .GetConstructors()
            .Where(x => x.GetParameters().Length == 1 && x.GetParameters().Single().ParameterType.IsClass);

        foreach (var constructor in constructors)
        {
            var slot = new MethodSlot(constructor, projectionType, aggregateType);
            Methods.Add(slot);
        }
    }

    public Type QuerySessionType { get; }

    protected override void validateMethod(MethodSlot method)
    {
        // Nothing, no special rules
    }

    protected override BindingFlags flags()
    {
        return BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    }

    public void BuildCreateMethod(GeneratedType generatedType, IStorageMapping aggregateMapping)
    {
        var returnType = IsAsync
            ? typeof(ValueTask<>).MakeGenericType(AggregateType)
            : AggregateType;

        var args = new[] { new Argument(typeof(IEvent), "@event"), new Argument(QuerySessionType, "session") };
        if (IsAsync)
        {
            args = args.Concat(new[] { new Argument(typeof(CancellationToken), "cancellation") }).ToArray();
        }

        var method = new GeneratedMethod(MethodName, returnType, args);
        method.AsyncMode = IsAsync ? AsyncMode.AsyncTask : AsyncMode.None;
        generatedType.AddMethod(method);

        var eventHandling = AddEventHandling(AggregateType, aggregateMapping, this);
        method.Frames.Add(eventHandling);

        method.Frames.ReturnNull();
    }

    public override IEventHandlingFrame CreateEventTypeHandler(Type aggregateType,
        IStorageMapping aggregateMapping, MethodSlot slot)
    {
        if (slot.Method is ConstructorInfo)
        {
            return new AggregateConstructorFrame(slot);
        }

        return new CreateAggregateFrame(slot);
    }
}
