using System.Collections.Concurrent;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace JasperFx.Events.InMemory;

/// <summary>
/// The event configuration of the in-memory prototyping store (jasperfx#964): stream identity, the
/// event types, and which metadata the store records on each event.
/// </summary>
public class InMemoryEventRegistry : EventRegistry, IAggregationSourceFactory<IInMemoryQuerySession>
{
    private readonly ConcurrentDictionary<string, Type> _aggregatesByAlias = new();

    /// <summary>Record the correlation id of the session on every event it appends.</summary>
    public bool CorrelationIdEnabled { get; set; }

    /// <summary>Record the causation id of the session on every event it appends.</summary>
    public bool CausationIdEnabled { get; set; }

    /// <summary>Record the session's headers on every event it appends.</summary>
    public bool HeadersEnabled { get; set; }

    /// <summary>Record the session's current user name on every event it appends.</summary>
    public bool UserNameEnabled { get; set; }

    /// <summary>
    /// Wrap event data in its envelope. An envelope already built with <c>BuildEvent</c> -- one the caller
    /// stamped headers onto, say -- is passed through rather than wrapped a second time.
    /// </summary>
    public override IEvent BuildEvent(object eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData is IEvent e)
        {
            var mapping = EventMappingFor(e.EventType);
            e.EventTypeName = mapping.EventTypeName;
            e.DotNetTypeName = mapping.DotNetTypeName;
            return e;
        }

        return base.BuildEvent(eventData);
    }

    /// <summary>
    /// The alias an aggregate type is recorded under on its streams. <see cref="EventRegistry"/> throws
    /// here by default, and <see cref="StreamAction.PrepareEvents"/> asks for it on every stream that
    /// names an aggregate type.
    /// </summary>
    public override string AggregateAliasFor(Type aggregateType)
    {
        var alias = aggregateType.FullName ?? aggregateType.Name;
        _aggregatesByAlias.TryAdd(alias, aggregateType);
        return alias;
    }

    /// <inheritdoc />
    public override Type AggregateTypeFor(string aggregateTypeName)
        => _aggregatesByAlias.TryGetValue(aggregateTypeName, out var type)
            ? type
            : throw new ArgumentOutOfRangeException(nameof(aggregateTypeName),
                $"No aggregate type has been recorded under the alias '{aggregateTypeName}'.");

    /// <summary>
    /// The live aggregator for an aggregate no projection was registered for (jasperfx#964): a
    /// single-stream projection closed over the aggregate's own identity type, so the source-generated
    /// evolver for its Apply / Create methods matches. <see cref="ProjectionGraph{TProjection,TOperations,TQuerySession}"/>
    /// falls back to this factory on the registry it was built with.
    /// </summary>
    IAggregatorSource<IInMemoryQuerySession>? IAggregationSourceFactory<IInMemoryQuerySession>.Build<TDoc>()
    {
        var projection = InMemoryAggregateIdentity.CreateLiveProjection<TDoc>(
            InMemoryAggregateIdentity.ResolveIdType(typeof(TDoc), StreamIdentity));

        projection.AssembleAndAssertValidity();

        foreach (var eventType in projection.IncludedEventTypes)
        {
            AddEventType(eventType);
        }

        return projection as IAggregatorSource<IInMemoryQuerySession>;
    }

    internal static NotSupportedException NotSupported(string member)
        => new($"{member} is not supported by the in-memory prototyping store (AddInMemoryStoreForPrototyping). " +
               "Switch to Marten, Polecat or Fisher when you need it.");
}
