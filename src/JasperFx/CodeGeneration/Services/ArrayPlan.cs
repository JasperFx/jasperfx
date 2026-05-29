using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

internal class ArrayFamily : ServiceFamily
{
    [UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
        Justification = "ArrayFamily wraps a collection-shaped serviceType (T[] / IEnumerable<T> / IList<T> / IReadOnlyList<T>) — there is no real constructor to preserve. The placeholder ServiceDescriptor exists so the family has a Default; the actual code emission uses CreateArrayFrame to write `new T[]{...}` source literal, never reflection.")]
    public ArrayFamily(Type serviceType) : base(serviceType, [new ServiceDescriptor(serviceType, serviceType, ServiceLifetime.Scoped)])
    {
        ElementType = serviceType.GetElementType() ?? serviceType.GenericTypeArguments[0];
    }

    public Type ElementType { get; }

    public override ServicePlan? BuildDefaultPlan(ServiceContainer graph, List<ServiceDescriptor> trail)
    {
        // IServiceProvider.GetServices<T>() never returns keyed registrations, so filter them out of
        // the element set instead of bailing the whole enumerable to runtime service location.
        var plans = graph.FindAll(ElementType, trail)
            .Where(x => !x.Descriptor.IsKeyedService)
            .ToList();

        // Optimization: an all-singleton enumerable can be built once and shared as a single field.
        if (plans.All(x => x.Lifetime == ServiceLifetime.Singleton))
        {
            return new SingletonPlan(Default);
        }

        // Otherwise build inline, element by element, with each element honoring its own lifetime:
        // scoped/transient => inline construction; a genuinely unbuildable element (e.g. opaque lambda
        // factory) => its own ServiceLocationPlan so only that element is service-located. A singleton
        // element cannot be injected by its (ambiguous) service type when the type has several
        // registrations, so route it through a keyed "mirror" (see EnumerableSingletons /
        // AddJasperFxEnumerableSingletonSupport) and inject it via [FromKeyedServices]. This mirrors
        // IServiceProvider.GetServices<T>() and avoids the old mixed-lifetime bail that tripped
        // ServiceLocationPolicy.NotAllowed for the whole IEnumerable<T>.
        var elementPlans = new List<ServicePlan>(plans.Count);
        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            elementPlans.Add(plan.Lifetime == ServiceLifetime.Singleton
                ? new SingletonPlan(EnumerableSingletons.KeyedMirror(ElementType, i))
                : plan);
        }

        return new ArrayPlan(ElementType, elementPlans, Default);
    }
}

internal class ArrayPlan : ServicePlan
{
    private readonly IReadOnlyList<ServicePlan> _elements;
    private readonly Type? _elementType;

    public ArrayPlan(Type elementType, IReadOnlyList<ServicePlan> elements, ServiceDescriptor @default) : base(@default)
    {
        _elements = elements;
        _elementType = elementType;
    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return _elements.Any(x => x.RequiresServiceProvider(method));
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        var text = "";
        foreach (var dependency in _elements)
        {
            if (dependency.RequiresServiceProvider(method))
            {
                text += System.Environment.NewLine;
                text += "Dependency: " + dependency + System.Environment.NewLine;
                text += dependency.WhyRequireServiceProvider(method);
                text += System.Environment.NewLine;
            }
        }
        
        return text;
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        var elements = _elements.Select(resolverVariables.Resolve).ToArray();
        return new CreateArrayFrame(ServiceType, _elementType, elements).Variable;
    }
}

public class CreateArrayFrame : SyncFrame
{
    private readonly Type _serviceType;
    private readonly Type _elementType;
    private readonly Variable[] _elements;

    public CreateArrayFrame(Type serviceType, Type elementType, Variable[] elements)
    {
        _serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        _elementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        _elements = elements;
        Variable = new Variable(serviceType, this);

        uses.AddRange(elements);
    }
    
    public Variable Variable { get; }
    
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Fail fast (jasperfx#381) if a mixed-lifetime singleton element was injected as null because
        // its keyed mirror is missing. Without this guard the enumerable silently contains a null that
        // surfaces far away as an NRE; the actionable message points straight at the missing
        // AddJasperFxEnumerableSingletonSupport() call.
        foreach (var element in _elements)
        {
            if (element is InjectedSingleton { Descriptor: { IsKeyedService: true } descriptor }
                && EnumerableSingletons.IsMirrorKey(descriptor.ServiceKey))
            {
                var message = EnumerableSingletons.MissingMirrorMessage(_elementType, descriptor.ServiceKey);
                writer.WriteLine(
                    $"if ({element.Usage} == null) throw new {typeof(InvalidOperationException).FullNameInCode()}({CodeFormatter.Write(message)});");
            }
        }

        writer.WriteLine($"{_serviceType.FullNameInCode()} {Variable.Usage} = new {_elementType.FullNameInCode()}[]{{{_elements.Select(x => x.Usage).Join(", ")}}};");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Mirror the C# fail-fast null guard for a missing keyed singleton mirror (jasperfx#381).
        foreach (var element in _elements)
        {
            if (element is InjectedSingleton { Descriptor: { IsKeyedService: true } descriptor }
                && EnumerableSingletons.IsMirrorKey(descriptor.ServiceKey))
            {
                var message = EnumerableSingletons.MissingMirrorMessage(_elementType, descriptor.ServiceKey);
                writer.WriteLine(
                    $"if isNull {element.FSharpUsage} then raise({typeof(InvalidOperationException).FSharpName()}({CodeFormatter.Write(message)}))");
            }
        }

        // F# array literal: [| e1; e2 |]. The element type is inferred, so no explicit annotation.
        writer.WriteLine($"{Variable.FSharpAssignmentUsage} = [| {_elements.Select(x => x.FSharpUsage).Join("; ")} |]");
        Next?.GenerateFSharpCode(method, writer);
    }
}