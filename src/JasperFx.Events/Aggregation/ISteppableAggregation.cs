using System.Text.Json;
using JasperFx.Core.Reflection;
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

    /// <summary>
    /// The same fan-out fold, given the store's serializer in both directions, which is what lets a failed
    /// apply put the fold back on the state from before it (jasperfx#1049).
    /// </summary>
    Task<MultiAggregateProjectionResult> BuildTimelinesAsync(
        IReadOnlyList<IEvent> events,
        TQuerySession session,
        ProjectionStateSerializer states,
        Func<IEvent, EventRecord> toRecord,
        IProjectionStepObserver? observer,
        CancellationToken cancellation)
        => BuildTimelinesAsync(events, session, states.Serialize, toRecord, observer, cancellation);

    /// <summary>
    /// Fold <paramref name="events"/> into one aggregate, one at a time, capturing the state around each
    /// as the JSON the store would persist. The single-aggregate form behind
    /// <see cref="IEventStore.RunProjectionAsync{TState}"/> and
    /// <see cref="IEventStore.RunProjectionByNameAsync"/>; a store reaches it through
    /// <see cref="ProjectionStepThrough{TQuerySession}"/>. Nothing is persisted.
    /// </summary>
    /// <remarks>
    /// Unlike <c>BuildTimelinesAsync</c> this does not slice or enrich: the list is taken as the events of
    /// this aggregate, and every record produces a step, including one <paramref name="toEvent"/> cannot read.
    /// </remarks>
    /// <param name="identity">The aggregate's identity. A single-stream projection reads it off each event instead.</param>
    /// <param name="events">Events to replay, in apply order.</param>
    /// <param name="toEvent">Maps a wire record to an <see cref="IEvent"/>, or to <see langword="null"/> when this process cannot read it.</param>
    /// <param name="session">Live query session handed to the projection's apply methods.</param>
    /// <param name="startingState">State to start from, as JSON; <see langword="null"/> starts with no aggregate.</param>
    /// <param name="states">The store's serializer.</param>
    /// <param name="cancellation">Cancellation token for the replay.</param>
    Task<ProjectionTimeline<JsonElement?>> BuildTimelineAsync(
        object identity,
        IReadOnlyList<EventRecord> events,
        Func<EventRecord, IEvent?> toEvent,
        TQuerySession session,
        JsonElement? startingState,
        ProjectionStateSerializer states,
        CancellationToken cancellation)
        => throw new NotSupportedException(
            $"{GetType().FullNameInCode()} does not implement the single-aggregate step-through. Use {nameof(BuildTimelinesAsync)}.");
}
