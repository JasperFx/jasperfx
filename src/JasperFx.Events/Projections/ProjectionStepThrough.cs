using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Aggregation;

namespace JasperFx.Events.Projections;

/// <summary>
///     Stateless projection step-through, the body of the three replay members on
///     <see cref="IEventStore" />: walks a fixed event list through a registered aggregation and reports
///     the state around each event. A store gets one from
///     <see cref="ProjectionGraph{TProjection,TOperations,TQuerySession}.StepThrough" /> and forwards to
///     it, opening the query session each call runs on (jasperfx#1049).
/// </summary>
/// <remarks>
///     <para>
///         <b>Every path folds through the projection's own dispatch</b>, which is
///         <see cref="ISteppableAggregation{TQuerySession}" />, so <c>Create</c>, <c>Apply</c>,
///         <c>ShouldDelete</c>, <c>DeleteEvent&lt;T&gt;()</c>, an <c>Evolve</c> override and a generated
///         evolver all behave as they do in the daemon.
///     </para>
///     <para>
///         <b>Nothing here can write.</b> The fold receives a query session and a do-nothing identity
///         setter, and never reaches <c>RaiseSideEffects</c>, projection storage or a message sink, so a
///         replay raises no events and publishes no messages. What it cannot police is the user's own
///         <c>Apply</c> body: code in there that reaches out to the world directly runs here as it does
///         anywhere else.
///     </para>
///     <para>
///         <b>Memory is proportional to the events times the size of the state.</b> A step holds a snapshot
///         of the state, a step's <c>After</c> is the next step's <c>Before</c>, and nothing is released
///         until the caller drops the timeline. An aggregate that grows with its stream is therefore
///         quadratic in the stream's length. No store bounds this and neither does this type: the bound is
///         the event list the caller passes in, which is why <c>projection-run</c> reads through
///         <c>--max-events</c> and says when it stopped early.
///     </para>
/// </remarks>
/// <typeparam name="TQuerySession">The store's query session type.</typeparam>
public sealed class ProjectionStepThrough<TQuerySession>
{
    private readonly Func<string, (ISteppableAggregation<TQuerySession> Aggregation, Type AggregateType)> _find;
    private readonly ProjectionStateSerializer _states;
    private readonly Func<EventRecord, IEvent?> _toEvent;

    internal ProjectionStepThrough(
        Func<string, (ISteppableAggregation<TQuerySession> Aggregation, Type AggregateType)> find,
        Func<EventRecord, IEvent?> toEvent, ProjectionStateSerializer states)
    {
        ArgumentNullException.ThrowIfNull(toEvent);
        ArgumentNullException.ThrowIfNull(states);

        _find = find;
        _toEvent = toEvent;
        _states = states;
    }

    /// <summary>
    ///     The strong-typed step-through behind <see cref="IEventStore.RunProjectionAsync{TState}" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each state is a detached copy read back from the JSON the store would persist, so a member
    ///         the serializer does not write is absent from every recorded step even though the fold
    ///         itself saw it. A state that cannot make that trip at all, or that comes back different from
    ///         what was written, throws <see cref="ProjectionStateRoundTripException" /> rather than
    ///         report a timeline it cannot vouch for.
    ///     </para>
    ///     <para>
    ///         <paramref name="startingState" /> is copied on the way in, so neither it nor anything a
    ///         previous timeline returned is changed by the fold.
    ///     </para>
    ///     <para>
    ///         One instance serves as a step's <c>After</c> and the next step's <c>Before</c>.
    ///     </para>
    /// </remarks>
    public async Task<ProjectionTimeline<TState>> RunProjectionAsync<TState>(string projectionName, object identity,
        IReadOnlyList<EventRecord> events, TState? startingState, TQuerySession session, CancellationToken ct)
    {
        var aggregation = findProjection<TState>(projectionName);
        ArgumentNullException.ThrowIfNull(events);

        var start = _states.Serialize(startingState);
        if (start is { } json)
        {
            _states.RoundTrip<TState>(json);
        }

        var timeline = await aggregation
            .BuildTimelineAsync(identity, events, _toEvent, session, start, _states, ct)
            .ConfigureAwait(false);

        var state = copyOf<TState>(timeline.Steps.Count == 0 ? timeline.FinalState : timeline.Steps[0].Before, null);
        var steps = new List<ProjectionStepResult<TState>>(timeline.Steps.Count);

        foreach (var step in timeline.Steps)
        {
            var after = copyOf<TState>(step.After, step.Event);
            steps.Add(new ProjectionStepResult<TState>(step.Event, state, after, step.Elapsed, step.Error));
            state = after;
        }

        return new ProjectionTimeline<TState>(steps, state);
    }

    /// <summary>
    ///     The JSON step-through behind <see cref="IEventStore.RunProjectionByNameAsync" />. No state is
    ///     read back to report it, so this answers for a state the typed form refuses.
    /// </summary>
    public async Task<ProjectionTimelineRaw> RunProjectionByNameAsync(string projectionName, object identity,
        IReadOnlyList<EventRecord> events, JsonElement? startingState, TQuerySession session, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(events);

        var timeline = await _find(projectionName).Aggregation
            .BuildTimelineAsync(identity, events, _toEvent, session, startingState, _states, ct)
            .ConfigureAwait(false);

        var steps = timeline.Steps
            .Select(x => new ProjectionStepResultRaw(x.Event, x.Before, x.After, x.Elapsed, x.Error?.Message))
            .ToList();

        return new ProjectionTimelineRaw(steps, timeline.FinalState);
    }

    /// <summary>
    ///     The fan-out step-through behind <see cref="IEventStore.RunMultiStreamProjectionAsync" />.
    /// </summary>
    /// <remarks>
    ///     An event <c>toEvent</c> cannot read is left out of the result entirely, unlike the single
    ///     aggregate forms, which report it as a step that changed nothing. Slicing is what says which
    ///     timeline an event belongs to, and it needs the event.
    /// </remarks>
    public async Task<MultiAggregateProjectionResult> RunMultiStreamProjectionAsync(string projectionName,
        IReadOnlyList<EventRecord> events, TQuerySession session, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(events);

        var aggregation = _find(projectionName).Aggregation;

        // Keyed by reference back to the record each event came from, because the fold hands back the
        // same IEvent instances and the caller supplied the record it wants to see.
        var recordFor = new Dictionary<IEvent, EventRecord>(ReferenceEqualityComparer.Instance);
        var domainEvents = new List<IEvent>(events.Count);

        foreach (var record in events)
        {
            if (_toEvent(record) is not { } domainEvent) continue;

            recordFor[domainEvent] = record;
            domainEvents.Add(domainEvent);
        }

        return await aggregation
            .BuildTimelinesAsync(domainEvents, session, _states,
                e => recordFor.TryGetValue(e, out var record) ? record : toRecord(e), null, ct)
            .ConfigureAwait(false);
    }

    private ISteppableAggregation<TQuerySession> findProjection<TState>(string projectionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);

        var (aggregation, aggregateType) = _find(projectionName);

        return aggregateType == typeof(TState)
            ? aggregation
            : throw new ArgumentException(
                $"Projection '{projectionName}' is over '{aggregateType.FullNameInCode()}', not '{typeof(TState).FullNameInCode()}'.",
                nameof(projectionName));
    }

    private TState? copyOf<TState>(JsonElement? json, EventRecord? at)
        => json is { } state ? _states.RoundTrip<TState>(state, at) : default;

    // A fan-out event, or an enrichment's WithData, is a new IEvent no record stands behind. Polecat and
    // Fisher looked those up unconditionally, so a projection with a FanOut threw KeyNotFoundException
    // out of the whole replay.
    private EventRecord toRecord(IEvent e) => new(e.Id, e.Sequence, e.Version,
        e.StreamKey ?? e.StreamId.ToString(), e.EventTypeName ?? e.EventType.Name,
        _states.Serialize(e.Data)!.Value, null, e.Timestamp, e.TenantId, null);
}
