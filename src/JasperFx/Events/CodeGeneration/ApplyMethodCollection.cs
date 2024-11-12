using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.CodeGeneration;

public class ApplyMethodCollection: MethodCollection
{
    public static readonly string MethodName = "Apply";

    public ApplyMethodCollection(Type projectionType, Type aggregateType, Type querySessionType): base(MethodName, projectionType,
        aggregateType)
    {
        QuerySessionType = querySessionType;
        LambdaName = "ProjectEvent";
        _validArgumentTypes.Add(querySessionType);
        _validArgumentTypes.Add(aggregateType);

        _validReturnTypes.Add(typeof(Task));
        _validReturnTypes.Add(typeof(void));
        _validReturnTypes.Add(aggregateType);
        _validReturnTypes.Add(typeof(Task<>).MakeGenericType(aggregateType));
    }

    public Type QuerySessionType { get; }

    protected override void validateMethod(MethodSlot method)
    {
        if (!method.DeclaredByAggregate && method.Method.GetParameters().All(x => x.ParameterType != AggregateType))
        {
            method.AddError($"Aggregate type '{AggregateType.FullNameInCode()}' is required as a parameter");
        }
    }

    public override IEventHandlingFrame CreateEventTypeHandler(Type aggregateType,
        IStorageMapping aggregateMapping, MethodSlot slot)
    {
        return new ApplyMethodCall(slot);
    }

    public void BuildApplyMethod(GeneratedType generatedType, IStorageMapping aggregateMapping)
    {
        var returnType = IsAsync
            ? typeof(ValueTask<>).MakeGenericType(AggregateType)
            : AggregateType;

        var args = new[]
        {
            new Argument(typeof(IEvent), "@event"), new Argument(AggregateType, "aggregate"),
            new Argument(QuerySessionType, "session")
        };

        if (IsAsync)
        {
            args = args.Concat(new[] { new Argument(typeof(CancellationToken), "cancellation") }).ToArray();
        }

        var method = new GeneratedMethod(MethodName, returnType, args);
        generatedType.AddMethod(method);

        var eventHandling = AddEventHandling(AggregateType, aggregateMapping, this);
        method.Frames.Add(eventHandling);


        method.Frames.Code("return {0};", new Use(AggregateType));
    }
}
