using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

internal class ConstructorPlan : ServicePlan
{
    public static ConstructorInfo[] FindPublicConstructorCandidates(Type implementationType)
    {
        // The codegen cannot use any non-public types
        if (implementationType is { IsPublic: false, IsNestedPublic: false })
        {
            return Array.Empty<ConstructorInfo>();
        }

        var candidates = implementationType.GetConstructors();

        if (candidates == null) return Array.Empty<ConstructorInfo>();

        // Filter out constructors we can't touch — but allow parameters with default values
        return candidates
            .Where(x => x.GetParameters().All(p => p.HasDefaultValue || CanPossiblyResolve(p.ParameterType)))
            .ToArray();
    }

    public static bool CanPossiblyResolve(Type type)
    {
        if (type.IsSimple()) return false;
        if (type.IsDateTime()) return false;
        if (type.IsNullable() && type.GetInnerTypeFromNullable().IsSimple()) return false;
        if (type.CanBeCastTo<Expression>()) return false;
        return true;
    }

    public static bool TryBuildPlan(List<ServiceDescriptor> trail, ServiceDescriptor descriptor, ServiceContainer graph,
        out ServicePlan plan)
    {
        if (trail.Contains(descriptor))
        {
            plan = new InvalidPlan(descriptor);
            return false;
        }
        
        trail.Add(descriptor);
        
        var implementationType = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;

        try
        {
            
            var constructors = FindPublicConstructorCandidates(implementationType);
        
            // If no public constructors, get out of here
            if (!constructors.Any())
            {
                plan = descriptor.ImplementationFactory != null ? new ServiceLocationPlan(descriptor) : new InvalidPlan(descriptor);
                return true;
            }

            if (constructors.Length == 1 && constructors[0].GetParameters().Length == 0)
            {
                plan = new ConstructorPlan(descriptor, constructors[0], Array.Empty<ServicePlan>());
                return true;
            }

            Func<ParameterInfo, bool> hasPlan = parameter =>
            {
                // Parameters with default values can always be satisfied
                if (parameter.HasDefaultValue) return true;

                if (parameter.TryGetAttribute<FromKeyedServicesAttribute>(out var att))
                {
                    var descriptor = graph
                        .RegistrationsFor(parameter.ParameterType)
                        .Where(x => x.IsKeyedService)
                        .FirstOrDefault(x => x.ServiceKey.ToString() == att.Key.ToString());

                    if (descriptor == null) return false;

                    return graph.PlanFor(descriptor, trail) is not InvalidPlan;
                }

                return graph.FindDefault(parameter.ParameterType, trail) is not InvalidPlan;
            };

            var constructor = constructors
                .Where(x => x.GetParameters().All(x => hasPlan(x)))
                .MinBy(x => x.GetParameters().Length);

            if (constructor != null)
            {
                // Only build dependency plans for parameters that don't have default values
                // or that can be resolved from DI despite having defaults
                var parameters = constructor.GetParameters();
                var dependencies = new List<ServicePlan>();
                var defaultParameterIndices = new List<int>();

                for (int i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];

                    if (p.TryGetAttribute<FromKeyedServicesAttribute>(out var att))
                    {
                        var keyedDescriptor = graph
                            .RegistrationsFor(p.ParameterType)
                            .Where(x => x.IsKeyedService)
                            .FirstOrDefault(x => x.ServiceKey.ToString() == att.Key.ToString());

                        dependencies.Add(graph.PlanFor(keyedDescriptor, trail));
                        continue;
                    }

                    // For parameters with default values: try DI first, but fall back to default
                    if (p.HasDefaultValue)
                    {
                        var defaultPlan = graph.FindDefault(p.ParameterType, trail);
                        if (defaultPlan is InvalidPlan || defaultPlan == null)
                        {
                            // Will use the parameter's default value — track the index
                            defaultParameterIndices.Add(i);
                            dependencies.Add(new DefaultValuePlan(p));
                            continue;
                        }

                        dependencies.Add(defaultPlan);
                        continue;
                    }

                    dependencies.Add(graph.FindDefault(p.ParameterType, trail));
                }

                if (dependencies.OfType<InvalidPlan>().Any() || dependencies.Any(x => x == null))
                {
                    plan = new InvalidPlan(descriptor);
                    return true;
                }

                plan = new ConstructorPlan(descriptor, constructor, dependencies.ToArray());
                return true;
            }
        
            plan = default;
            return false;
        }
        finally
        {
            trail.Remove(descriptor);
        }
    }

    public ServicePlan[] Dependencies { get; }

    public ConstructorPlan(ServiceDescriptor descriptor, ConstructorInfo constructor, ServicePlan[] dependencies) : base(descriptor)
    {
        if (descriptor.Lifetime == ServiceLifetime.Singleton)
            throw new ArgumentOutOfRangeException(nameof(descriptor),
                $"Only {ServiceLifetime.Scoped} or {ServiceLifetime.Transient} lifecycles are valid");

        if (constructor.GetParameters().Length != dependencies.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(dependencies),
                "The constructor parameter count does not match the number of supplied dependencies");
        }

        if (dependencies.Any(x => x == null))
        {
            throw new ArgumentOutOfRangeException(nameof(dependencies), "Missing dependencies");
        }
        
        Constructor = constructor;

        Dependencies = dependencies;
    }

    public ConstructorInfo Constructor { get; }

    public override IEnumerable<Type> FindDependencies()
    {
        foreach (var dependency in Dependencies)
        {
            if (dependency is DefaultValuePlan) continue;

            yield return dependency.ServiceType;

            foreach (var type in dependency.FindDependencies())
            {
                yield return type;
            }
        }
    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return Dependencies.Any(x => x.RequiresServiceProvider(method));
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        var text = "";
        foreach (var dependency in Dependencies)
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
        var implementationType = Descriptor.IsKeyedService ? Descriptor.KeyedImplementationType : Descriptor.ImplementationType;
        var frame = new ConstructorFrame(implementationType, Constructor);
        if (implementationType.CanBeCastTo<IDisposable>() || implementationType.CanBeCastTo<IAsyncDisposable>())
        {
            frame.Mode = ConstructorCallMode.UsingNestedVariable;
        }
        
        for (int i = 0; i < Dependencies.Length; i++)
        {
            var argument = resolverVariables.Resolve(Dependencies[i]);
            frame.Parameters[i] = argument;
        }

        return frame.Variable;
    }
}