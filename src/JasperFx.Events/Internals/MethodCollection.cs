using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Internals;

[UnconditionalSuppressMessage("Trimming", "IL2070:DynamicallyAccessedMembers",
    Justification = "Class-level: reflects PublicMethods on the projection / aggregate Type to discover Apply/Create/ShouldDelete/Project handlers. Both types are preserved at the registration boundary on the caller side.")]
internal abstract class MethodCollection
{
    private static readonly Dictionary<int, Type> _funcBaseTypes = new()
    {
        { 1, typeof(Func<>) },
        { 2, typeof(Func<,>) },
        { 3, typeof(Func<,,>) },
        { 4, typeof(Func<,,,>) },
        { 5, typeof(Func<,,,,>) },
        { 6, typeof(Func<,,,,,>) },
        { 7, typeof(Func<,,,,,,>) },
        { 8, typeof(Func<,,,,,,,>) }
    };

    private static readonly Dictionary<int, Type> _actionBaseTypes = new()
    {
        { 1, typeof(Action<>) },
        { 2, typeof(Action<,>) },
        { 3, typeof(Action<,,>) },
        { 4, typeof(Action<,,,>) },
        { 5, typeof(Action<,,,,>) },
        { 6, typeof(Action<,,,,,>) },
        { 7, typeof(Action<,,,,,,>) },
        { 8, typeof(Action<,,,,,,,>) }
    };

    protected readonly List<Type> _validArgumentTypes = new();
    protected readonly List<Type> _validReturnTypes = new();

    protected MethodCollection(string methodName, Type projectionType, Type aggregateType)
        : this([methodName], projectionType, aggregateType)
    {
    }

    protected MethodCollection(string[] methodNames, Type projectionType, Type aggregateType)
    {
        _validArgumentTypes.Add(typeof(CancellationToken));

        MethodNames.AddRange(methodNames);

        ProjectionType = projectionType;

        AggregateType = aggregateType;

        if (projectionType != null)
        {
            projectionType.GetMethods(flags())
                .Where(x => MethodNames.Contains(x.Name))
                .Where(x => !x.HasAttribute<JasperFxIgnoreAttribute>())
                .Each(method => addMethodSlot(method, false));
        }

        if (aggregateType != null)
        {
            aggregateType.GetMethods(flags())
                .Where(x => MethodNames.Contains(x.Name))
                .Where(x => !x.HasAttribute<JasperFxIgnoreAttribute>())
                .Each(method => addMethodSlot(method, true));
        }
        
        IsAsync = Methods.Select(x => x.Method).OfType<MethodInfo>().Any(x => x.IsAsync());
    }

    public Type ProjectionType { get; }

    internal IReadOnlyList<Type> ValidArgumentTypes => _validArgumentTypes;

    public IReadOnlyList<Type> ValidReturnTypes => _validReturnTypes;

    public Type AggregateType { get; }

    public List<string> MethodNames { get; } = new();

    public List<MethodSlot> Methods { get; } = new();

    public bool IsAsync { get; private set; }

    protected virtual BindingFlags flags()
    {
        return BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    }

    internal static Type[] AllEventTypes(params MethodCollection[] methods)
    {
        return methods.SelectMany(x => x.EventTypes())
            .Distinct()
            .ToArray();
    }

    internal IEnumerable<Type> EventTypes()
    {
        // A handler taking a C# union handles every case of it. Events are stored as the active case
        // (EventRegistry.BuildEvent unwraps), so the union type itself never appears in the event store
        // and is replaced here by its cases rather than kept beside them.
        return Methods.Where(x => x.EventType != null).SelectMany(x => x.EventType!.ExpandToCaseTypes()).Distinct();
    }

    internal abstract void validateMethod(MethodSlot method);

    private void addMethodSlot(MethodInfo method, bool declaredByAggregate)
    {
        // Latch against Create using something with primitives
        if (method.GetParameters().Any(x => x.ParameterType.IsSimple())) return;
        
        var slot = new MethodSlot(method, AggregateType)
        {
            HandlerType = declaredByAggregate ? AggregateType : ProjectionType,
            DeclaredByAggregate = declaredByAggregate
        };

        if (ValidArgumentTypes.Contains(slot.EventType))
        {
            slot.DisallowEventType();
        }
        
        Methods.Add(slot);
    }


    public static MethodSlot[] FindInvalidMethods(Type projectionType, params MethodCollection[] collections)
    {
        var methodNames = collections.SelectMany(x => x.MethodNames).Concat([nameof(IMetadataApplication.ApplyMetadata), "RaiseSideEffects"]).Distinct().ToArray();

        var invalidMethods = projectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => !x.HasAttribute<JasperFxIgnoreAttribute>())
            .Where(x => x.DeclaringType!.Assembly != typeof(MethodCollection).Assembly)
            .Where(x => !x.DeclaringType!.Assembly.HasAttribute<JasperFxAssemblyAttribute>())
            .Where(x => x.GetBaseDefinition().DeclaringType!.Assembly != typeof(MethodCollection).Assembly)
            .Where(x => !x.GetBaseDefinition().DeclaringType!.Assembly.HasAttribute<JasperFxAssemblyAttribute>())
            .Where(x => x.DeclaringType != typeof(object))
            .Where(x => !x.IsSpecialName)
            .Where(x => !methodNames.Contains(x.Name))
            .Select(x => MethodSlot.InvalidMethodName(x, methodNames))
            .ToList();

        foreach (var collection in collections)
        {
            // We won't validate the methods that come through inline Lambdas
            foreach (var method in collection.Methods)
            {
                method.Validate(collection);
                collection.validateMethod(method); // hook for unusual rules
            }

            invalidMethods.AddRange(collection.Methods.Where(x => x.Errors.Any()));
        }

        return invalidMethods.ToArray();
    }

    /// <summary>
    ///     jasperfx#1053: refuse a C# union event parameter anywhere but a bare Apply(TUnion). Runs whether or
    ///     not a source-generated dispatcher was found, because with one the rest of the method validation is
    ///     skipped, and a misplaced union would otherwise be listened for and silently never dispatched.
    /// </summary>
    internal static void AssertNoUnsupportedUnionUsage(Type projectionType, params MethodCollection[] collections)
    {
        var offenders = collections.SelectMany(x => x.Methods).Where(x => x.HasUnsupportedUnionUsage()).ToArray();
        if (offenders.Length == 0) return;

        var messages = new List<string> { $"Projection {projectionType.FullNameInCode()} has validation errors:" };
        foreach (var slot in offenders)
        {
            messages.Add($"{slot.Signature()}");
            messages.Add($" - {MethodSlot.UnsupportedUnionUsage} ('{slot.EventType!.FullNameInCode()}')");
        }

        throw new InvalidProjectionException(messages.ToArray());
    }

    public bool IsEmpty()
    {
        return !Methods.Any();
    }

}
