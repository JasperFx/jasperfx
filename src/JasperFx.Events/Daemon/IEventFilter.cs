namespace JasperFx.Events.Daemon;

/// <summary>
/// Data-shape filter that an event-store implementation can consult when loading or
/// dispatching events. Lets a projection / subscription declare "only these event
/// types" without forcing the loader to invent its own SQL fragment shape.
/// </summary>
/// <remarks>
/// This is intentionally storage-agnostic — concrete event-store implementations
/// translate <see cref="IEventFilter"/> into their own query language (Marten emits an
/// <c>ISqlFragment</c>, Polecat builds a <c>dotnet_type</c> allowlist).
/// </remarks>
public interface IEventFilter
{
    /// <summary>
    /// True when this filter does not restrict the event stream — the loader can skip
    /// applying any predicate and return everything in the requested range.
    /// </summary>
    bool MatchesAnyEvent { get; }

    /// <summary>
    /// The set of event types this filter is willing to deliver. When
    /// <see cref="MatchesAnyEvent"/> is true this collection is unused; otherwise the
    /// loader is expected to restrict the result set to events whose runtime type
    /// matches an entry in this collection.
    /// </summary>
    IReadOnlyCollection<Type> EventTypes { get; }
}
