using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// The discovery bridge for overlays: adapts an <see cref="EventModelDefinition"/> — a
/// registered subclass, a ready instance, or an inline lambda — to
/// <see cref="IEventModelDefinitionSource"/> (jasperfx#687 §3). Instantiates the definition
/// (from DI when it is a type), runs <see cref="EventModelDefinition.Configure"/>, and
/// snapshots the result as an <see cref="EventModelDescriptor"/>.
/// </summary>
public sealed class EventModelDefinitionSource : IEventModelDefinitionSource
{
    /// <summary>URI scheme for overlay sources: <c>event-model://{name}</c>.</summary>
    public const string Scheme = "event-model";

    private readonly string _name;
    private readonly Func<IServiceProvider, EventModelDefinition?> _resolve;

    private EventModelDefinitionSource(string name, Func<IServiceProvider, EventModelDefinition?> resolve)
    {
        _name = name;
        _resolve = resolve;
        Subject = new Uri($"{Scheme}://{Uri.EscapeDataString(name)}");
    }

    /// <summary>Wrap a ready definition instance.</summary>
    public static EventModelDefinitionSource For(EventModelDefinition definition)
        => new(definition.Name, _ => definition);

    /// <summary>
    /// Wrap a definition type. Resolved from the service provider if registered there, otherwise
    /// constructed through <see cref="ActivatorUtilities"/> so constructor dependencies still
    /// come from DI.
    /// </summary>
    public static EventModelDefinitionSource For(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type definitionType)
    {
        if (!typeof(EventModelDefinition).IsAssignableFrom(definitionType) || definitionType.IsAbstract)
        {
            throw new ArgumentException(
                $"{definitionType.FullName} is not a concrete {nameof(EventModelDefinition)} subclass",
                nameof(definitionType));
        }

        return new EventModelDefinitionSource(definitionType.Name, services =>
            (services.GetService(definitionType) ?? ActivatorUtilities.CreateInstance(services, definitionType))
            as EventModelDefinition);
    }

    /// <summary>Wrap a definition type.</summary>
    public static EventModelDefinitionSource For<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDefinition>()
        where TDefinition : EventModelDefinition
        => For(typeof(TDefinition));

    /// <summary>Wrap an inline overlay.</summary>
    public static EventModelDefinitionSource For(string name, Action<EventModelBuilder> configure)
        => new(name, _ => new LambdaDefinition(name, configure));

    /// <inheritdoc />
    public Uri Subject { get; }

    /// <inheritdoc />
    public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        var definition = _resolve(services);
        if (definition is null) return Task.FromResult<EventModelDescriptor?>(null);

        var builder = new EventModelBuilder();
        definition.Configure(builder);
        return Task.FromResult<EventModelDescriptor?>(builder.Build(definition.Name));
    }

    private sealed class LambdaDefinition : EventModelDefinition
    {
        private readonly Action<EventModelBuilder> _configure;

        public LambdaDefinition(string name, Action<EventModelBuilder> configure)
        {
            Name = name;
            _configure = configure;
        }

        public override string Name { get; }

        public override void Configure(EventModelBuilder builder) => _configure(builder);
    }
}
