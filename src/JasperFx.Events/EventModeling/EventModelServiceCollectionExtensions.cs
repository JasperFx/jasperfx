using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Registration API for Event Model sources (jasperfx#687 §3). Every registration is an
/// <see cref="IEventModelDefinitionSource"/> singleton; <see cref="EventModelDiscovery"/>
/// enumerates them.
/// </summary>
public static class EventModelServiceCollectionExtensions
{
    /// <summary>
    /// Register an <see cref="EventModelDefinition"/> subclass as an overlay source. The type is
    /// resolved from DI at discovery time (or constructed with its dependencies from DI).
    /// </summary>
    public static IServiceCollection AddEventModel<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDefinition>(
        this IServiceCollection services)
        where TDefinition : EventModelDefinition
        => services.AddEventModelSource(EventModelDefinitionSource.For<TDefinition>());

    /// <summary>
    /// Register a discovered <see cref="EventModelDefinition"/> subclass as an overlay source.
    /// </summary>
    public static IServiceCollection AddEventModel(
        this IServiceCollection services,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type definitionType)
        => services.AddEventModelSource(EventModelDefinitionSource.For(definitionType));

    /// <summary>Register a ready definition instance as an overlay source.</summary>
    public static IServiceCollection AddEventModel(this IServiceCollection services, EventModelDefinition definition)
        => services.AddEventModelSource(EventModelDefinitionSource.For(definition));

    /// <summary>Register an inline overlay.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">Name of the model the overlay contributes to.</param>
    /// <param name="configure">The overlay.</param>
    public static IServiceCollection AddEventModel(this IServiceCollection services, string name, Action<EventModelBuilder> configure)
        => services.AddEventModelSource(EventModelDefinitionSource.For(name, configure));

    /// <summary>
    /// Register every concrete <see cref="EventModelDefinition"/> subclass exported by
    /// <paramref name="assembly"/>.
    /// </summary>
    [RequiresUnreferencedCode("Enumerates Assembly.ExportedTypes to find EventModelDefinition subclasses. AOT-publishing apps should register each definition explicitly with AddEventModel<T>().")]
    public static IServiceCollection AddEventModelsFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(EventModelDefinition).IsAssignableFrom(type)) continue;
            services.AddEventModel(type);
        }

        return services;
    }

    /// <summary>
    /// Register any <see cref="IEventModelDefinitionSource"/> — an overlay adapter, a generated
    /// source, a Wolverine-derived source — so <see cref="EventModelDiscovery"/> sees it.
    /// </summary>
    public static IServiceCollection AddEventModelSource(this IServiceCollection services, IEventModelDefinitionSource source)
    {
        services.AddSingleton(source);
        return services;
    }

    /// <summary>
    /// Register an <see cref="IEventModelDefinitionSource"/> implementation type.
    /// </summary>
    public static IServiceCollection AddEventModelSource<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSource>(
        this IServiceCollection services)
        where TSource : class, IEventModelDefinitionSource
    {
        services.AddSingleton<IEventModelDefinitionSource, TSource>();
        return services;
    }
}

/// <summary>
/// The enumeration path (jasperfx#687 §3): walk every registered
/// <see cref="IEventModelDefinitionSource"/> and assemble the full picture.
/// </summary>
public static class EventModelDiscovery
{
    /// <summary>
    /// Ask every registered source for its descriptor. Sources that return null are skipped.
    /// Returned in registration order — derived sources should be registered before overlays so
    /// <see cref="Assemble"/> lets derived roles win.
    /// </summary>
    public static async Task<IReadOnlyList<EventModelDescriptor>> DiscoverAsync(IServiceProvider services, CancellationToken token = default)
    {
        var descriptors = new List<EventModelDescriptor>();
        foreach (var source in services.GetServices<IEventModelDefinitionSource>())
        {
            var descriptor = await source.TryCreateAsync(services, token).ConfigureAwait(false);
            if (descriptor is not null) descriptors.Add(descriptor);
        }

        return descriptors;
    }

    /// <summary>
    /// Fold the discovered descriptors into one model per name — overlays onto derived sources,
    /// slices by name — and return them in first-appearance order.
    /// </summary>
    public static IReadOnlyList<EventModelDescriptor> Assemble(IEnumerable<EventModelDescriptor> descriptors)
    {
        var order = new List<string>();
        var byName = new Dictionary<string, List<EventModelDescriptor>>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (!byName.TryGetValue(descriptor.Name, out var list))
            {
                list = new List<EventModelDescriptor>();
                byName[descriptor.Name] = list;
                order.Add(descriptor.Name);
            }

            list.Add(descriptor);
        }

        return order.Select(name => EventModelDescriptor.Merge(name, byName[name])).ToList();
    }

    /// <summary>
    /// <see cref="DiscoverAsync"/> then <see cref="Assemble"/>: the full picture, one descriptor
    /// per model name.
    /// </summary>
    public static async Task<IReadOnlyList<EventModelDescriptor>> AssembleAsync(IServiceProvider services, CancellationToken token = default)
        => Assemble(await DiscoverAsync(services, token).ConfigureAwait(false));
}
