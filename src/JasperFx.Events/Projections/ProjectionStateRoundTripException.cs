using JasperFx.Core.Reflection;
using JasperFx.Descriptors;

namespace JasperFx.Events.Projections;

/// <summary>
///     A projection's state cannot be carried through the store's serializer, so a step-through cannot
///     say what the state was at each step.
/// </summary>
/// <remarks>
///     Thrown rather than recorded on a step (jasperfx#1049). <c>Error</c> on a step means the
///     projection's apply method failed on that event, and this is neither about that event nor about
///     the projection: it is a property of the state's type, and every step would carry it.
/// </remarks>
public class ProjectionStateRoundTripException: InvalidOperationException
{
    public ProjectionStateRoundTripException(Type stateType, EventRecord? @event, string problem,
        Exception? innerException = null)
        : base(
            $"The state of type '{stateType.FullNameInCode()}' {problem}"
            + (@event is null ? "" : $" at event {@event.Sequence} ('{@event.EventTypeName}')")
            + ". A projection step-through records each step through the store's serializer, because the fold "
            + "mutates the aggregate in place and a live reference would show the final state on every step. "
            + "Give the state a shape the store's serializer can read back, or use RunProjectionByNameAsync, "
            + "which reports the state as JSON and does not need to.", innerException)
    {
        StateType = stateType;
        Event = @event;
    }

    /// <summary>
    ///     The state type that cannot make the trip.
    /// </summary>
    public Type StateType { get; }

    /// <summary>
    ///     The event whose resulting state failed, or <see langword="null" /> for the starting state.
    /// </summary>
    public EventRecord? Event { get; }
}
