using System.Collections.Concurrent;

namespace JasperFx.Events.InMemory;

/// <summary>
/// The event configuration of the in-memory prototyping store (jasperfx#964): stream identity, the
/// event types, and which metadata the store records on each event.
/// </summary>
public class InMemoryEventRegistry : EventRegistry
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

    internal static NotSupportedException NotSupported(string member)
        => new($"{member} is not supported by the in-memory prototyping store (AddInMemoryStoreForPrototyping). " +
               "Switch to Marten, Polecat or Fisher when you need it.");
}
