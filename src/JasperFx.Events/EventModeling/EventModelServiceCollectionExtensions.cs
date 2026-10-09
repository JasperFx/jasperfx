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
    /// <paramref name="assembly"/>. Reads the source-generated manifest when the assembly has one, as
    /// <see cref="AddDiscoveredEventModels"/> does, and scans <c>ExportedTypes</c> otherwise.
    /// </summary>
    [RequiresUnreferencedCode("Enumerates Assembly.ExportedTypes to find EventModelDefinition subclasses when the assembly has no source-generated manifest. AOT-publishing apps should use AddDiscoveredEventModels(assembly) with JasperFx.SourceGenerator, or register each definition with AddEventModel<T>().")]
    public static IServiceCollection AddEventModelsFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        if (TryReadDefinitionManifest(assembly, out var definitionTypes))
        {
            foreach (var type in definitionTypes) services.AddEventModel(type);
            return services;
        }

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(EventModelDefinition).IsAssignableFrom(type)) continue;
            services.AddEventModel(type);
        }

        return services;
    }

    /// <summary>
    /// Register every <see cref="EventModelDefinition"/> subclass in <paramref name="assembly"/> from
    /// its source-generated <c>JasperFx.Generated.DiscoveredEventModels</c> manifest — AOT-safe, with no
    /// type enumeration (jasperfx#993).
    /// </summary>
    /// <remarks>
    /// The manifest is emitted by <c>JasperFx.SourceGenerator</c> into every assembly that references
    /// JasperFx.Events, empty when there are no definitions. An assembly built without the generator has
    /// none, and falls back to the same reflective scan as <see cref="AddEventModelsFromAssembly"/> —
    /// which is not trim-safe, so an AOT app has to reference the generator in each assembly that
    /// declares definitions.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "The reflective scan is reached only for an assembly without the source-generated DiscoveredEventModels manifest. Trim/AOT apps reference JasperFx.SourceGenerator, which emits the manifest as ordinary code in each consuming assembly; an app that omits it has opted into the scan.")]
    public static IServiceCollection AddDiscoveredEventModels(this IServiceCollection services, Assembly assembly)
        => services.AddEventModelsFromAssembly(assembly);

    /// <summary>
    /// The definition types listed by <paramref name="assembly"/>'s generated manifest, or false when it
    /// has none. The lookup is by name because the manifest is <c>internal</c> to the assembly it lists.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "JasperFx.Generated.DiscoveredEventModels is emitted by JasperFx.SourceGenerator into the consuming assembly as ordinary code; when it has been trimmed away the lookup degrades to 'no manifest'.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075:DynamicallyAccessedMembers",
        Justification = "Same as IL2026: DefinitionTypes is a generated public static property of the manifest type.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072:DynamicallyAccessedMembers",
        Justification = "The manifest roots each listed type's public constructors with [DynamicDependency], which is what EventModelDefinitionSource.For needs.")]
    internal static bool TryReadDefinitionManifest(Assembly assembly,
        [NotNullWhen(true)] out IReadOnlyList<Type>? definitionTypes)
    {
        definitionTypes = null;
        if (assembly.IsDynamic) return false;

        var manifestType = assembly.GetType("JasperFx.Generated.DiscoveredEventModels");
        var property = manifestType?.GetProperty("DefinitionTypes", BindingFlags.Public | BindingFlags.Static);
        if (property?.GetValue(null) is not IEnumerable<Type> types) return false;

        definitionTypes = types
            .Where(x => !x.IsAbstract && typeof(EventModelDefinition).IsAssignableFrom(x))
            .ToList();

        return true;
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

    /// <summary>
    /// Register the store-derived rung (jasperfx#825): one <see cref="SlicePattern.View"/> slice per
    /// registered projection, read out of the event store's own registry.
    /// </summary>
    /// <remarks>
    /// Called by a store's own <c>AddMarten</c> / <c>AddPolecat</c> / <c>AddFisher</c>, which is the
    /// only place that knows how its <see cref="IEventStore"/> is registered — hence the resolver.
    /// Omit it for a host that registered the shared interface directly.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="stores">Resolves the stores to describe. Defaults to every registered <see cref="IEventStore"/>.</param>
    /// <param name="modelName">Name of the model these slices contribute to. Must match the other sources' model name, since discovery groups by it before merging slices.</param>
    /// <param name="subject">Identifies this source. A store registering one instance per store should mint a distinct subject for each, since it is the fallback stamped onto <see cref="EventModelSliceDescriptor.Origin"/> (jasperfx#836).</param>
    public static IServiceCollection AddProjectionEventModelSource(
        this IServiceCollection services,
        Func<IServiceProvider, IEnumerable<IEventStore>>? stores = null,
        string? modelName = null,
        Uri? subject = null)
    {
        var name = modelName ?? ProjectionEventModelSource.DefaultModelName;

        var uri = subject ?? ProjectionEventModelSource.DefaultSubject;

        return services.AddEventModelSource(stores is null
            ? new ProjectionEventModelSource { ModelName = name, Subject = uri }
            : new ProjectionEventModelSource(stores) { ModelName = name, Subject = uri });
    }
}

/// <summary>
/// The enumeration path (jasperfx#687 §3): walk every registered
/// <see cref="IEventModelDefinitionSource"/> and assemble the full picture.
/// </summary>
public static class EventModelDiscovery
{
    /// <summary>
    /// The name of <em>the application's</em> model — what a definition with no
    /// <see cref="EventModelDefinition.Name"/> contributes to (jasperfx#992).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JasperFxOptions.ServiceName"/> when one is registered. That is the name the
    /// code-derived sources use: Wolverine names its model after <c>WolverineOptions.ServiceName</c> and
    /// carries it back to <see cref="JasperFxOptions"/>, and Marten, Polecat and Fisher name theirs after
    /// the same value unless a store sets its own model name. Otherwise the entry assembly's name, which
    /// is what <see cref="JasperFxOptions.ServiceName"/> defaults to; failing that,
    /// <see cref="ProjectionEventModelSource.DefaultModelName"/>.
    /// </para>
    /// <para>
    /// Public so a source that derives a model from code can name it the same way rather than repeat
    /// the rule.
    /// </para>
    /// </remarks>
    public static string ApplicationModelName(IServiceProvider services)
    {
        var serviceName = services.GetService<JasperFxOptions>()?.ServiceName;
        if (!string.IsNullOrWhiteSpace(serviceName)) return serviceName;

        var entry = Assembly.GetEntryAssembly()?.GetName().Name;
        return string.IsNullOrWhiteSpace(entry) ? ProjectionEventModelSource.DefaultModelName : entry;
    }

    /// <summary>
    /// Ask every registered source for its descriptor, stamping each one with the source's
    /// <see cref="IEventModelDefinitionSource.Provenance"/> (jasperfx#703). Sources that return null
    /// are skipped. Returned in registration order, which <see cref="Assemble"/> now uses only to
    /// break ties between sources on the same rung.
    /// </summary>
    public static async Task<IReadOnlyList<EventModelDescriptor>> DiscoverAsync(IServiceProvider services, CancellationToken token = default)
    {
        var descriptors = new List<EventModelDescriptor>();
        foreach (var source in services.GetServices<IEventModelDefinitionSource>())
        {
            var descriptor = await source.TryCreateAsync(services, token).ConfigureAwait(false);
            if (descriptor is not null) descriptors.Add(descriptor.WithProvenance(source.Provenance));
        }

        return descriptors;
    }

    /// <summary>
    /// Fold the discovered descriptors into one model per name — slices by name, each role decided by
    /// the <see cref="EventModelProvenance"/> ladder — and return them in first-appearance order.
    /// </summary>
    /// <remarks>
    /// ⚠️ Several models in one host are legal, supported, and never an error here — a modular
    /// monolith whose modules each name their own model through <c>StoreOptions.EventModelName</c>
    /// produces exactly that. A caller that folds this list back down to one descriptor loses a model
    /// name outright and has its slices merged into another model's (jasperfx#837);
    /// <see cref="AssembleSetAsync"/> is the shape that carries the service and the models as a pair.
    /// </remarks>
    public static IReadOnlyList<EventModelDescriptor> Assemble(IEnumerable<EventModelDescriptor> descriptors)
        => EventModelDescriptor.GroupByName(descriptors);

    /// <summary>
    /// <see cref="DiscoverAsync"/> then <see cref="Assemble"/>: the full picture, one descriptor
    /// per model name.
    /// </summary>
    public static async Task<IReadOnlyList<EventModelDescriptor>> AssembleAsync(IServiceProvider services, CancellationToken token = default)
        => Assemble(await DiscoverAsync(services, token).ConfigureAwait(false));

    /// <summary>
    /// <see cref="AssembleAsync"/>, scoped to the service that hosts the models — the shape an
    /// exporter should put on the wire (jasperfx#837).
    /// </summary>
    /// <remarks>
    /// The service dimension has to come from the caller: <c>WolverineOptions.ServiceName</c>, a
    /// CritterWatch manifest id, an assembly name. Nothing in this library knows what service it is
    /// running inside, which is precisely why the exporters reached for the service name as the
    /// <em>model</em> name and collapsed the models to fit.
    /// </remarks>
    /// <param name="services">The service provider to enumerate sources from.</param>
    /// <param name="serviceName">The service hosting the models.</param>
    /// <param name="token">Cancellation token.</param>
    public static async Task<EventModelSetDescriptor> AssembleSetAsync(IServiceProvider services, string serviceName,
        CancellationToken token = default)
        => EventModelSetDescriptor.For(serviceName, await DiscoverAsync(services, token).ConfigureAwait(false));
}
