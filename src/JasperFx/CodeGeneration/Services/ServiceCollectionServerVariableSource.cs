using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

public class ServiceCollectionServerVariableSource : IServiceVariableSource
{
    public const string UsingScopedContainerDirectly = $@"Using the scoped provider service location approach
because at least one dependency is directly using IServiceProvider or has an opaque Lambda registration that has {nameof(ServiceLifetime)} of either {nameof(ServiceLifetime.Scoped)} or {nameof(ServiceLifetime.Transient)}";
    
    private readonly ServiceContainer _services;
    private bool _usesScopedContainerDirectly;
    private readonly List<StandInVariable> _standins = new();
    private readonly List<InjectedSingleton> _fields = new();
    private Variable _scoped;
    private List<ServiceLocationReport> _serviceLocations = [];
    private bool _replacedServiceProvider;

    public ServiceCollectionServerVariableSource(IServiceContainer services)
    {
        _services = (ServiceContainer?)services;
        _scoped = newScopedProvider();
    }

    /// <summary>
    ///     Frames to emit immediately after the service-location child scope is created, before anything
    ///     is resolved out of it. One frame is built per generated method, so each may hold per-method
    ///     state. Frames implementing <see cref="IUsesServiceProviderFrame" /> are handed the scoped
    ///     provider variable; a frame that finds nothing to do is expected to emit nothing.
    /// </summary>
    /// <remarks>
    ///     This is how a host seeds the child scope with instances the generated code already owns --
    ///     Wolverine primes it with the handler's MessageContext and the outbox-enrolled persistence
    ///     session, so a service-located IMessageContext or IDocumentSession is that same instance
    ///     rather than a second, un-enrolled one.
    ///
    ///     Attaching here rather than from a frame in the generated method is deliberate. A frame can
    ///     only look for the scoped provider during <c>MethodFrameArranger</c>'s first resolution pass,
    ///     but the scope for an opaque scoped/transient registration is not created until
    ///     <see cref="ReplaceVariables" /> runs after it -- so a frame-based activator silently found
    ///     nothing and attached nothing for exactly the chains that most needed it. See wolverine#4171.
    ///
    ///     Nothing is attached when <see cref="ReplaceServiceProvider" /> has supplied an external
    ///     provider (e.g. Wolverine.HTTP's <c>httpContext.RequestServices</c>): no scope is created
    ///     there, and that container belongs to the host, not to the generated method.
    /// </remarks>
    public List<Func<SyncFrame>> ScopePostProcessorSources { get; } = new();

    private Variable newScopedProvider()
    {
        var creation = new ScopedContainerCreation();
        foreach (var source in ScopePostProcessorSources)
        {
            creation.AddPostProcessor(source());
        }

        return creation.Scoped;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067:DynamicallyAccessedMembers",
        Justification = "IServiceVariableSource.Matches(Type) doesn't carry DAM on its Type parameter, so this impl can't propagate the [DAM(PublicConstructors)] constraint that ServiceContainer.CouldResolve needs. The codegen-Variable resolution path is reached only from compiled-handler discovery where the candidate types come from typeof(T) expressions or already-registered ServiceDescriptors — both carry constructor preservation via their own surface.")]
    public bool Matches(Type type)
    {
        return _services.CouldResolve(type);
    }

    public bool TryFindKeyedService(Type type, string key, out Variable? variable)
    {
        variable = default;
        
        var descriptor = _services.RegistrationsFor(type).Where(x => x.IsKeyedService)
            .FirstOrDefault(x => Equals(x.ServiceKey, key));

        if (descriptor == null)
        {
            return false;
        }

        var plan = _services.PlanFor(descriptor, []);

        variable = createVariableForPlan(type, plan);
        return variable != null;
    }

    public Variable Create(Type type)
    {
        if (type == typeof(IServiceProvider))
        {
            _usesScopedContainerDirectly = true;
            return _scoped;
        }

        var plan = _services.FindDefault(type, new());
        return createVariableForPlan(type, plan);
    }

    private Variable createVariableForPlan(Type type, ServicePlan? plan)
    {
        if (plan is InvalidPlan)
        {
            throw new NotSupportedException($"Cannot build service type {type.FullNameInCode()} in any way");
        }

        if (plan is null)
        {
            throw new NotSupportedException($"Unable to create a service variable for type {type.FullNameInCode()}");
        }
        
        if (plan.Lifetime == ServiceLifetime.Singleton)
        {
            var field = _fields.FirstOrDefault(x => x.Descriptor == plan.Descriptor);
            if (field == null)
            {
                field = new InjectedSingleton(plan.Descriptor);
                _fields.Add(field);
            }

            return field;
        }

        var standin = new StandInVariable(plan);
        _standins.Add(standin);

        return standin;
    }

    public void ReplaceServiceProvider(Variable serviceProvider)
    {
        if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));

        if (serviceProvider.VariableType != typeof(IServiceProvider))
            throw new ArgumentOutOfRangeException(nameof(serviceProvider),
                $"VariableType has to be {typeof(IServiceProvider).FullNameInCode()}");

        _replacedServiceProvider = true;
        _scoped = serviceProvider;
    }

    // GH-2991: the runtime (DynamicTypeLoader) resolves a fresh transient IServiceVariableSource per
    // ICodeFile, so a ReplaceServiceProvider() call there is naturally isolated to one file. The CLI
    // codegen paths (DynamicCodeBuilder write/preview/test) reuse a SINGLE shared instance across every
    // file, and ReplaceServiceProvider latches _replacedServiceProvider = true permanently (StartNewMethod
    // only re-creates the default scope when it is false). Reset between files so a per-file
    // ServiceProviderSource override (e.g. HTTP's httpContext.RequestServices) does not leak into the
    // following files.
    public void ResetServiceProvider()
    {
        _replacedServiceProvider = false;
        _scoped = newScopedProvider();
    }

    public ServiceLocationReport[] ServiceLocations()
    {
        return _serviceLocations.ToArray();
    }

    public void ReplaceVariables(IMethodVariables method)
    {
        var requiresLocation = _standins.Where(x => x.Plan.RequiresServiceProvider(method)).ToArray();
        if (_usesScopedContainerDirectly || requiresLocation.Any())
        {
            if (_usesScopedContainerDirectly)
            {
                _serviceLocations.Add(new ServiceLocationReport(new ServiceDescriptor(typeof(IServiceProvider), typeof(IServiceProvider), ServiceLifetime.Scoped), "Directly using scoped IServiceProvider"));
            }

            foreach (var standInVariable in requiresLocation)
            {
                _serviceLocations.Add(new ServiceLocationReport(standInVariable.Plan.Descriptor, standInVariable.Plan.WhyRequireServiceProvider(method)));
            }
            
            useServiceProvider(method);
        }
        else
        {
            useInlineConstruction(method);
        }
    }

    public void StartNewType()
    {
        StartNewMethod();
        _fields.Clear();
    }

    public void StartNewMethod()
    {
        if (!_replacedServiceProvider)
        {
            // A fresh scope -- and therefore a fresh set of postprocessor frames -- per generated
            // method, because those frames carry per-method variable state.
            _scoped = newScopedProvider();
        }

        _usesScopedContainerDirectly = false;
        _serviceLocations = [];
        _standins.Clear();
    }

    private void useServiceProvider(IMethodVariables method)
    {
        var written = false;
        foreach (var standin in _standins)
        {
            // Keyed services must keep their key when dragged onto the service-location path,
            // otherwise the generated code emits GetRequiredService<T> and loses the key. See GH-2878.
            var descriptor = standin.Plan.Descriptor;
            var serviceKey = descriptor is { IsKeyedService: true } ? descriptor.ServiceKey : null;
            var frame = new GetServiceFromScopedContainerFrame(_scoped, standin.VariableType, serviceKey);
            var variable = frame.Variable;

            // Write description of why this had to use the nested container
            if (standin.Plan.RequiresServiceProvider(method))
            {
                var comment = standin.Plan.WhyRequireServiceProvider(method);

                if (_usesScopedContainerDirectly && !written)
                {
                    comment += System.Environment.NewLine;
                    comment += UsingScopedContainerDirectly;

                    written = true;
                }

                frame.MultiLineComment(comment);
            }
            else if (_usesScopedContainerDirectly && !written)
            {
                frame.MultiLineComment(UsingScopedContainerDirectly);
                written = true;
            }

            standin.UseInner(variable);
        }

        var duplicates = _standins.GroupBy(x => x.Usage).Where(x => x.Count() > 1);
        foreach (var duplicate in duplicates)
        {
            var usage = 0;
            foreach (var standinVariable in duplicate) standinVariable.OverrideName(standinVariable.Usage + ++usage);
        }
    }
    
    private void useInlineConstruction(IMethodVariables method)
    {
        // THIS NEEDS TO BE SCOPED PER METHOD!!!
        var variables = new ServiceVariables(method, _fields);
        foreach (var standin in _standins)
        {
            var variable = variables.Resolve(standin.Plan);
            standin.UseInner(variable);
        }

        foreach (var singleton in variables.OfType<InjectedSingleton>())
        {
            singleton.IsOnlyOne = !_services.HasMultiplesOf(singleton.VariableType);
        }

        variables.MakeNamesUnique();
    }
}