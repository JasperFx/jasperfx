using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace JasperFx.Events;

public partial class EventRegistry
{
    private readonly ConcurrentDictionary<Type, string> _aliasByAggregateType = new();
    private readonly ConcurrentDictionary<string, Type> _aggregateTypeByAlias = new();

    private readonly ConcurrentDictionary<string, Type> _eventTypeByDotNetName = new();

    // Each unresolvable name, stamped with the _eventTypeGeneration it was probed under. The stamp is how a
    // later registration invalidates a miss, see RegisterDotNetTypeNames.
    private readonly ConcurrentDictionary<string, long> _missedDotNetNames = new();
    private long _eventTypeGeneration;

    /// <summary>
    ///     The name persisted for an aggregate type. Every store has its own, on purpose: this is stored
    ///     data, so the base class caches the answer and has no opinion on what it is.
    /// </summary>
    /// <remarks>
    ///     Called at most once per type by <see cref="AggregateAliasFor" />, whose cache it fills. Throws by
    ///     default rather than picking a name, because a name a store never chose would be persisted.
    /// </remarks>
    protected virtual string AliasFor(Type aggregateType)
    {
        throw new NotSupportedException("Override in a derived class to support aggregate alias resolution.");
    }

    /// <summary>
    ///     Find the aggregate type for an alias no <see cref="AggregateAliasFor" /> call has seen yet, such
    ///     as a stream written by another process. Null, the default, when there is none.
    /// </summary>
    /// <remarks>
    ///     A result is cached. A miss is not, so an alias that becomes known later still resolves, and an
    ///     override can expect to be called again.
    /// </remarks>
    protected virtual Type? FindAggregateType(string aggregateTypeName)
    {
        return null;
    }

    /// <summary>
    ///     The aggregate type recorded under an alias, or null when this deployment has none. Unlike
    ///     <see cref="AggregateTypeFor" /> this never throws: a stream tagged by a deployment that knew a
    ///     type this one does not must still report its version and timestamps.
    /// </summary>
    public Type? TryResolveAggregateType(string? aggregateTypeName)
    {
        if (string.IsNullOrEmpty(aggregateTypeName))
        {
            return null;
        }

        if (_aggregateTypeByAlias.TryGetValue(aggregateTypeName, out var known))
        {
            return known;
        }

        var found = FindAggregateType(aggregateTypeName);
        return found == null ? null : _aggregateTypeByAlias.GetOrAdd(aggregateTypeName, found);
    }

    /// <summary>
    ///     Resolve a .NET type from the name stored on an event row, or null for a type this deployment
    ///     does not know. Runs once per event row hydrated, so the answer is memoized either way.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Misses are cached because they are the expensive case: a stream holding foreign event types
    ///         would otherwise repeat <see cref="LoadEventType" />'s name parse and assembly probe per row.
    ///     </para>
    ///     <para>
    ///         A cached miss is only good until the next event type registration, see
    ///         <see cref="RegisterDotNetTypeNames" />.
    ///     </para>
    /// </remarks>
    public Type? ResolveEventType(string? dotNetTypeName)
    {
        if (string.IsNullOrEmpty(dotNetTypeName))
        {
            return null;
        }

        if (_eventTypeByDotNetName.TryGetValue(dotNetTypeName, out var known))
        {
            return known;
        }

        // Read before the probe, not after. A registration that lands while the probe runs moves the
        // generation past this stamp, so the miss about to be recorded is already out of date and the next
        // row asks again, where stamping afterwards would hide that registration from it.
        var generation = Interlocked.Read(ref _eventTypeGeneration);

        if (_missedDotNetNames.TryGetValue(dotNetTypeName, out var missedUnder) && missedUnder == generation)
        {
            return null;
        }

        var type = LoadEventType(dotNetTypeName);
        if (type == null)
        {
            _missedDotNetNames[dotNetTypeName] = generation;
            return null;
        }

        _missedDotNetNames.TryRemove(dotNetTypeName, out _);
        return _eventTypeByDotNetName.GetOrAdd(dotNetTypeName, type);
    }

    /// <summary>
    ///     As <see cref="ResolveEventType" />, for a path that cannot continue without the type.
    /// </summary>
    /// <exception cref="UnknownEventTypeException">The name resolves to no type in this deployment.</exception>
    public Type TypeForDotNetName(string dotNetTypeName)
    {
        return ResolveEventType(dotNetTypeName) ?? throw new UnknownEventTypeException(dotNetTypeName);
    }

    /// <summary>
    ///     Turn a stored name into a type, for a name no registration has seeded. Override to recognize a
    ///     name that no longer loads as written, such as a type that moved, and defer to the base for the
    ///     rest. Compare the whole name there: a substring also matches other people's event types.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2057:UnrecognizedTypeName",
        Justification = "Resolving the name a row was written with is what this method is. The type is kept by the application's own registration of it, and one nothing keeps resolves to null, which every caller of ResolveEventType treats as an event this deployment does not know.")]
    protected virtual Type? LoadEventType(string dotNetTypeName)
    {
        return Type.GetType(dotNetTypeName);
    }

    /// <summary>
    ///     Tell the name cache about an event type. A store that overrides
    ///     <see cref="EventMappingFor" /> calls this when it builds a mapping, because nothing else tells
    ///     the base class a registration happened.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is how a registration invalidates a cached miss, in two steps. The type's own names are
    ///         seeded, and a seeded name beats any miss, so <see cref="ResolveEventType" /> never reaches
    ///         <see cref="LoadEventType" /> for it. That covers a type <c>Type.GetType</c> could never have
    ///         found, such as one in a non-default load context.
    ///     </para>
    ///     <para>
    ///         Then the generation moves, which expires every other miss. Registering one type can be what
    ///         loaded the assembly behind a different name that missed, so each is probed once more on its
    ///         next read, and is cached again if it still misses. That costs one probe per missed name per
    ///         registration, and nothing here walks the cache.
    ///     </para>
    /// </remarks>
    protected void RegisterDotNetTypeNames(IEventType mapping)
    {
        var eventType = mapping.EventType;

        _eventTypeByDotNetName[mapping.DotNetTypeName] = eventType;

        // Both of the other shapes turn up in stored event metadata as well
        if (eventType.AssemblyQualifiedName is { } assemblyQualifiedName)
        {
            _eventTypeByDotNetName[assemblyQualifiedName] = eventType;
        }

        if (eventType.FullName is { } fullName)
        {
            _eventTypeByDotNetName[fullName] = eventType;
        }

        // After the seeding, so that a reader which sees the new generation also sees the names
        Interlocked.Increment(ref _eventTypeGeneration);
    }
}
