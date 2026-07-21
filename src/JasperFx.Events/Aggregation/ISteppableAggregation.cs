using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

/// <summary>
/// Type-erased entry point for the step-instrumented aggregation fold. Implemented
/// by <see cref="JasperFxAggregationProjectionBase{TDoc,TId,TOperations,TQuerySession}"/>
/// so a store can look a projection up by name (where the concrete <c>TDoc</c>/<c>TId</c>
/// are not statically known) and drive the real slice → group → enrich → fold path,
/// capturing a per-event before/after timeline for every resulting identity.
/// </summary>
/// <typeparam name="TQuerySession">The store's query session type used for slicing and enrichment.</typeparam>
public interface ISteppableAggregation<TQuerySession>
{
    /// <summary>
    /// Fold <paramref name="events"/> through the projection's real execution path —
    /// slicing, grouping, <c>EnrichEventsAsync</c>, then applying each event one at a
    /// time via the projection's <c>Create</c>/<c>Apply</c>/<c>ShouldDelete</c> conventions
    /// (i.e. <c>EvolveAsync</c>) — producing one <see cref="ProjectionTimelineRaw"/> per
    /// aggregate identity. Nothing is persisted; this is a stateless replay.
    /// </summary>
    /// <remarks>
    /// Enrichment reads present-day reference data even when replaying historical events —
    /// the fold calls the same <c>EnrichEventsAsync</c> the async daemon uses, against the
    /// supplied live session. Callers replaying old event sets should treat any enriched
    /// values as reflecting the current state of that reference data, not the state at the
    /// time the events were originally recorded.
    /// </remarks>
    /// <param name="events">Events to replay, in global apply order.</param>
    /// <param name="session">Live query session used for slicing and enrichment.</param>
    /// <param name="serialize">
    /// Serializes an aggregate snapshot (or <see langword="null"/>) to JSON using the store's
    /// own serializer so the captured state matches what the store would persist.
    /// </param>
    /// <param name="toRecord">Maps a folded <see cref="IEvent"/> back to its wire <see cref="EventRecord"/>.</param>
    /// <param name="observer">Optional observer that receives each step as it is folded; may be <see langword="null"/>.</param>
    /// <param name="cancellation">Cancellation token for the replay.</param>
    Task<MultiAggregateProjectionResult> BuildTimelinesAsync(
        IReadOnlyList<IEvent> events,
        TQuerySession session,
        Func<object?, JsonElement?> serialize,
        Func<IEvent, EventRecord> toRecord,
        IProjectionStepObserver? observer,
        CancellationToken cancellation);
}
