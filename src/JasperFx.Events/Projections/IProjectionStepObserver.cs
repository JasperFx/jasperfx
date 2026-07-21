using JasperFx.Descriptors;

namespace JasperFx.Events.Projections;

/// <summary>
/// Receives per-event, per-identity step results as a projection is folded over
/// a fixed event set by the step-instrumented build path
/// (<see cref="ISteppableAggregation{TQuerySession}.BuildTimelinesAsync"/>).
/// Used by the event store explorer / CritterWatch projection-stepthrough to
/// stream a projection's before/after state as it is being computed, rather than
/// only receiving the completed timeline. The fold awaits each callback, so an
/// observer may push to a channel, hub, or socket without racing the fold.
/// </summary>
public interface IProjectionStepObserver
{
    /// <summary>
    /// Invoked once per event applied to a single aggregate identity, in apply order.
    /// Called immediately after the event has been folded (successfully or not) so
    /// the observer sees steps as they happen.
    /// </summary>
    /// <param name="identity">String form of the aggregate identity this step belongs to.</param>
    /// <param name="step">Before/after JSON state, elapsed apply time, and any error for this event.</param>
    /// <param name="cancellation">Cancellation token for the overall replay.</param>
    ValueTask ObserveAsync(string identity, ProjectionStepResultRaw step, CancellationToken cancellation);
}
