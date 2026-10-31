using JasperFx.MultiTenancy;

namespace JasperFx.Events.Appending;

/// <summary>
///     Where the actions in a plan came from, which decides whether their expected version means anything.
/// </summary>
public enum AppendOrigin
{
    /// <summary>
    ///     A caller's unit of work: an expected version on the action is a claim, and it is enforced.
    /// </summary>
    UnitOfWork,

    /// <summary>
    ///     Events a projection raised. <c>EventSlice.BuildOperations</c> sets the expected version from
    ///     the slice's own event count, which is the stream's version only if the projection has seen every
    ///     event on it, so it is a guess and nobody's claim. It is discarded here, as Polecat's
    ///     <c>BuildRaisedEventAppendsAsync</c> already does (polecat#420). Enforcing it, as Fisher's
    ///     batch does, stops a shard on a stream it has only partly seen (jasperfx#1051).
    /// </summary>
    RaisedByProjection
}

/// <summary>
///     Everything about a unit of work that a plan depends on and a stream action does not carry, so that
///     <see cref="ClientSideAppendPlanner" /> reads no clock, no session and no database.
/// </summary>
/// <remarks>
///     The required members have no default on purpose: each one is a place the stores used to differ, and
///     a default would be one of them silently winning for whoever forgot.
/// </remarks>
public sealed record ClientSideAppendContext
{
    /// <summary>
    ///     The session's tracing and user metadata, and which of it this store persists.
    /// </summary>
    public required IMetadataContext Metadata { get; init; }

    /// <summary>
    ///     The moment of this unit of work, stamped on every event that does not carry its own.
    /// </summary>
    /// <remarks>
    ///     An input rather than a clock read, for two reasons. The events of a unit of work share a moment
    ///     the way they share a transaction. And a store whose column is coarser than a
    ///     <see cref="DateTimeOffset" /> has to round this first: Fisher's keeps milliseconds, so stamping
    ///     raw ticks would leave the inline view ahead of every later read of the same event.
    /// </remarks>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    ///     The store's append mode. It changes nothing in the decision table, because a client-side plan
    ///     reads the stream in every mode. It changes who supplies <see cref="IEvent.Sequence" />: see
    ///     <see cref="ReservedSequences" />.
    /// </summary>
    public required EventAppendMode AppendMode { get; init; }

    /// <summary>
    ///     The store's event tenancy. Under <see cref="TenancyStyle.Conjoined" /> the same id in two tenants
    ///     is two streams, and under <see cref="TenancyStyle.Single" /> it is one.
    /// </summary>
    public required TenancyStyle Tenancy { get; init; }

    /// <summary>
    ///     Where these actions came from. A plan covers one origin.
    /// </summary>
    public AppendOrigin Origin { get; init; } = AppendOrigin.UnitOfWork;

    /// <summary>
    ///     Sequences already reserved for this unit of work, consumed in plan order. Required by
    ///     <see cref="EventAppendMode.Rich" />, which promises inline projections the sequence of every
    ///     event, and ignored by the Quick modes, where the database assigns it and the store reads it back.
    /// </summary>
    public IReadOnlyList<long>? ReservedSequences { get; init; }

    /// <summary>
    ///     The exceptions refusals become.
    /// </summary>
    public AppendRefusals Refusals { get; init; } = AppendRefusals.Shared;
}
