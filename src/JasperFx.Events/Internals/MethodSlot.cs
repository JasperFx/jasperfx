using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Internals;

[UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
    Justification = "Class-level: assigns reflective MethodInfo / Type results to DAM-annotated targets during handler-slot construction. Source types preserved at the registration boundary on the caller side.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: Type.MakeGenericType / FastExpressionCompiler.CompileFast on the slot's invocation path — runtime code generation. AOT consumers rely on source-generated handlers.")]
public class MethodSlot
{
    public static readonly string NoEventType =
        "No event type can be determined. The argument for the event should be named '@event'";

    private readonly List<string> _errors = new();

    public MethodSlot(MethodInfo method, Type aggregateType)
    {
        Method = method;
        EventType = method.GetEventType(aggregateType);
        ReturnType = method.ReturnType;
        DeclaringType = method.DeclaringType!;
    }

    public MethodSlot(Setter setter, MethodInfo method, Type eventType)
    {
        Setter = setter;
        Method = method;
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        DeclaringType = method.DeclaringType!;
        ReturnType = method.ReturnType;
        HandlerType = setter.VariableType;
    }

    public MethodSlot(ConstructorInfo constructor, Type projectionType, Type aggregateType)
    {
        if (constructor.GetParameters().Length > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(constructor), "Only single argument constructor functions can be used here.");
        }

        var parameterType = constructor.GetParameters().Single().ParameterType;
        EventType = parameterType.Closes(typeof(IEvent<>))
            ? parameterType.GetGenericArguments().Single()
            : parameterType;

        EventType ??= constructor.GetParameters().Where(x => x.ParameterType != typeof(IEvent)).SingleOrDefaultIfMany()?.ParameterType;

        if (EventType == null)
            throw new InvalidOperationException(
                $"Unable to identify the event type for constructor function {constructor.DeclaringType}({constructor.GetParameters().Select(x => x.ParameterType.ShortNameInCode()).Join(", ")})");

        ReturnType = aggregateType;
        HandlerType = projectionType;
        DeclaringType = aggregateType;
        Method = constructor;
    }

    public Setter? Setter { get; }
    public MethodBase Method { get; }

    public Type DeclaringType { get; }

    public Type ReturnType { get; }
    // Is this ever null?
    public Type HandlerType { get; set; } = null!;

    public Type? EventType { get; private set; }

    public IReadOnlyList<string> Errors => _errors;
    public bool DeclaredByAggregate { get; set; }

    public IEnumerable<Type> ReferencedTypes()
    {
        yield return DeclaringType;
        if (EventType != null)
        {
            yield return EventType;
        }
    }

    public string Signature()
    {
        var description =
            $"{Method.Name}({Method.GetParameters().Select(x => x.ParameterType.NameInCode()).Join(", ")})";
        if (ReturnType != typeof(void))
        {
            description += $" : {ReturnType.NameInCode()}";
        }

        return description;
    }

    internal void Validate(MethodCollection collection)
    {
        if (EventType == null)
        {
            _errors.Add(NoEventType);
        }
        else
        {
            validateArguments(collection);
            validateUnionUsage();
        }

        if (collection.ValidReturnTypes.Any() && !collection.ValidReturnTypes.Contains(ReturnType))
        {
            var message =
                $"Return type '{ReturnType.FullNameInCode()}' is invalid. The valid options are {collection.ValidArgumentTypes.Select(x => x.FullNameInCode()).Join(", ")}";
            AddError(message);
        }
    }

    public static readonly string UnsupportedUnionUsage =
        "C# union event parameters are only supported as the bare event parameter of an Apply method, " +
        "Apply(TUnion). Events are stored as the union's active case, so take the case types here, or " +
        "IEvent and switch on e.Data";

    // jasperfx#1053: events are stored as the active case of a union, and only a bare Apply(TUnion) is
    // dispatched per case by the source generator. Anywhere else (Create, ShouldDelete, Project, a
    // constructor, IEvent<TUnion>) the projection would listen for the cases and then match none of them.
    internal bool HasUnsupportedUnionUsage()
    {
        if (EventType == null || !EventType.IsUnion()) return false;

        var wrapped = typeof(IEvent<>).MakeGenericType(EventType);
        return Method.Name != ApplyMethodName || Method.GetParameters().Any(x => x.ParameterType == wrapped);
    }

    private void validateUnionUsage()
    {
        if (HasUnsupportedUnionUsage())
        {
            _errors.Add($"{UnsupportedUnionUsage} ('{EventType!.FullNameInCode()}' in {Signature()})");
        }
    }

    private const string ApplyMethodName = "Apply";

    internal void AddError(string error)
    {
        _errors.Add(error);
    }

    private void validateArguments(MethodCollection collection)
    {
        List<Type?> possibleTypes = [..collection.ValidArgumentTypes, EventType, typeof(IEvent)];

        if (EventType != null)
        {
            possibleTypes.Add(typeof(IEvent<>).MakeGenericType(EventType));
        }

        if (collection.AggregateType != null)
        {
            possibleTypes.Fill(collection.AggregateType);
        }

        foreach (var parameter in Method.GetParameters())
        {
            var type = parameter.ParameterType;
            if (!possibleTypes.Contains(type))
            {
                _errors.Add(
                    $"Parameter of type '{type.FullNameInCode()}' is not supported. Valid options are {possibleTypes.Select(x => x.FullNameInCode()).Join(", ")}");
            }
        }
    }

    public static MethodSlot InvalidMethodName(MethodInfo methodInfo, string[] methodNames)
    {
        var slot = new MethodSlot(methodInfo, null!);
        slot._errors.Add(
            $"Unrecognized method name '{methodInfo.Name}'. Either mark with [JasperFxIgnore] or use one of {methodNames.Select(x => $"'{x}'").Join(", ")}");

        return slot;
    }

    public void DisallowEventType()
    {
        EventType = null;
        _errors.Add(NoEventType);
    }
}