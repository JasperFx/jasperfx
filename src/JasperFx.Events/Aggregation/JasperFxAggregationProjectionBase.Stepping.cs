using System.Diagnostics;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
    : ISteppableAggregation<TQuerySession>
    where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    private static readonly NulloIdentitySetter<TDoc, TId> _stepIdentitySetter = new();

    /// <inheritdoc />
    public Task<MultiAggregateProjectionResult> BuildTimelinesAsync(
        IReadOnlyList<IEvent> events,
        TQuerySession session,
        Func<object?, JsonElement?> serialize,
        Func<IEvent, EventRecord> toRecord,
        IProjectionStepObserver? observer,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(serialize);

        return BuildTimelinesAsync(events, session, new ProjectionStateSerializer(serialize), toRecord, observer,
            cancellation);
    }

    /// <inheritdoc />
    public async Task<MultiAggregateProjectionResult> BuildTimelinesAsync(
        IReadOnlyList<IEvent> events,
        TQuerySession session,
        ProjectionStateSerializer states,
        Func<IEvent, EventRecord> toRecord,
        IProjectionStepObserver? observer,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(toRecord);

        // Run the *real* fan-out path: the same slicer the async daemon uses, so
        // multi-stream projections fan a single event list out across every identity
        // they touch and single-stream projections collapse to exactly one slice.
        var slicer = BuildSlicer(session);
        var groups = await slicer.SliceAsync(events).ConfigureAwait(false);

        var byIdentity = new Dictionary<string, ProjectionTimelineRaw>();

        foreach (var groupObject in groups)
        {
            if (groupObject is not SliceGroup<TDoc, TId> group)
            {
                continue;
            }

            // Faithful to the daemon: enrichment happens after slicing, before applying.
            await EnrichEventsAsync(group, session, cancellation).ConfigureAwait(false);

            foreach (var slice in group.Slices)
            {
                var identity = slice.Id?.ToString() ?? string.Empty;
                var timeline = await foldSliceAsync(slice, session, states, toRecord, observer, cancellation)
                    .ConfigureAwait(false);

                // A multi-stream slicer never produces two slices for the same identity within
                // a group, but guard anyway so a duplicate can't throw and lose the whole run.
                byIdentity[identity] = timeline;
            }
        }

        return new MultiAggregateProjectionResult(Name, byIdentity);
    }

    /// <inheritdoc />
    public async Task<ProjectionTimeline<JsonElement?>> BuildTimelineAsync(
        object identity,
        IReadOnlyList<EventRecord> events,
        Func<EventRecord, IEvent?> toEvent,
        TQuerySession session,
        JsonElement? startingState,
        ProjectionStateSerializer states,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(toEvent);
        ArgumentNullException.ThrowIfNull(states);

        var identityOf = stepIdentitySource(identity);

        // Read from the JSON, not handed over, so the fold has no way to reach whatever object the caller
        // started from. Polecat and Fisher folded straight into theirs, which is how passing
        // page1.FinalState into the next page changed page1.
        var snapshot = startingState is { } start ? states.Deserialize<TDoc>(start) : default;
        var before = states.Serialize(snapshot);

        var steps = new List<ProjectionStepResult<JsonElement?>>(events.Count);

        foreach (var record in events)
        {
            cancellation.ThrowIfCancellationRequested();

            Exception? error = null;
            var started = Stopwatch.GetTimestamp();

            try
            {
                // Null is an event this process cannot read, normally a type it does not know. It still
                // gets a step, showing it changed nothing, because a console pointed at a deployment
                // that knows fewer types than the store holds should not lose the timeline over it.
                if (toEvent(record) is { } @event)
                {
                    (snapshot, error) = await stepAsync(session, snapshot, identityOf(@event), @event, cancellation)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // A record that cannot be read at all fails on its step, like an apply.
                cancellation.ThrowIfCancellationRequested();
                error = e;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);

            (snapshot, var after) = settleStep(snapshot, before, error, states, record);

            steps.Add(new ProjectionStepResult<JsonElement?>(record, before, after, elapsed, error));
            before = after;
        }

        return new ProjectionTimeline<JsonElement?>(steps, before);
    }

    private async Task<ProjectionTimelineRaw> foldSliceAsync(
        EventSlice<TDoc, TId> slice,
        TQuerySession session,
        ProjectionStateSerializer states,
        Func<IEvent, EventRecord> toRecord,
        IProjectionStepObserver? observer,
        CancellationToken cancellation)
    {
        var identity = slice.Id?.ToString() ?? string.Empty;
        var steps = new List<ProjectionStepResultRaw>();

        TDoc? snapshot = default;
        JsonElement? before = null;

        foreach (var @event in slice.Events())
        {
            cancellation.ThrowIfCancellationRequested();

            var record = toRecord(@event);

            var start = Stopwatch.GetTimestamp();
            (snapshot, var error) = await stepAsync(session, snapshot, slice.Id, @event, cancellation)
                .ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(start);

            (snapshot, var after) = settleStep(snapshot, before, error, states, record);

            var step = new ProjectionStepResultRaw(record, before, after, elapsed, error?.Message);
            steps.Add(step);
            before = after;

            if (observer != null)
            {
                await observer.ObserveAsync(identity, step, cancellation).ConfigureAwait(false);
            }
        }

        return new ProjectionTimelineRaw(steps, before);
    }

    /// <summary>
    ///     One event through the real dispatch, in the shape the daemon gives it. A failure comes back
    ///     rather than out, because a step-through exists to show where a projection breaks, and throwing
    ///     would hide every step after the first bad one.
    /// </summary>
    /// <remarks>
    ///     <see cref="IAggregator{T,TSession}.BuildAsync" /> cannot do this job, and that is why the store
    ///     replays that called it did not model a delete. It discards the action, and a delete leaves the
    ///     snapshot non-null: <c>DeleteEvent&lt;T&gt;()</c> returns it untouched alongside
    ///     <see cref="ActionType.Delete" />. Nor could they recover the one case it does signal,
    ///     an <c>Evolve</c> returning null, having written <c>built ?? current</c>.
    /// </remarks>
    private async ValueTask<(TDoc? Snapshot, Exception? Error)> stepAsync(TQuerySession session, TDoc? snapshot,
        TId id, IEvent @event, CancellationToken cancellation)
    {
        try
        {
            // What a slice's FastForwardForCompacting does in the daemon. A compacted stream's marker is
            // the only thing left of the history before it, so without this every state before the next
            // event is missing all of it.
            var (fastForwarded, events) = Compacted<TDoc>.MaybeFastForward(snapshot, [@event]);
            if (events.Count == 0) return (fastForwarded, null);

            var (result, action) = await DetermineActionAsync(session, snapshot, id, _stepIdentitySetter, events,
                cancellation).ConfigureAwait(false);

            return action switch
            {
                ActionType.Delete or ActionType.HardDelete => (default, null),

                // Same as the daemon, so a captured state carries the version and metadata it would be
                // persisted with.
                ActionType.Nothing => (result, null),
                _ => (tryApplyMetadata(events, result, id, _stepIdentitySetter).Item2, null)
            };
        }
        catch (Exception e)
        {
            // Being cancelled is not the event's failure, and it can arrive here wrapped.
            cancellation.ThrowIfCancellationRequested();

            // Unwrap what the fold path adds, so the step carries the projection's own failure rather
            // than the daemon's "Failure to apply event #n".
            return (snapshot, e is ApplyEventException { InnerException: { } inner } ? inner : e);
        }
    }

    /// <summary>
    ///     Capture the state a step ended in, and put the fold back on the state from before the step
    ///     when an apply failed part way through.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A step's <c>After</c> is documented as equal to <c>Before</c> when the apply threw, but an
    ///         <c>Apply</c> that adds to a list and then divides by zero has already changed the aggregate.
    ///         That half-applied state then carried into every later step, under an event reported as
    ///         having failed.
    ///     </para>
    ///     <para>
    ///         Restoring is a trip through the serializer, which drops whatever it does not write, so it
    ///         happens only when the failed apply actually changed the state. The usual failure throws
    ///         before touching anything and leaves the fold on its live instance.
    ///     </para>
    /// </remarks>
    private (TDoc? Snapshot, JsonElement? After) settleStep(TDoc? snapshot, JsonElement? before, Exception? error,
        ProjectionStateSerializer states, EventRecord record)
    {
        var after = states.Serialize(snapshot);

        if (error is null || !states.CanDeserialize || before is not { } restorable
            || (after is { } attempted && JsonElement.DeepEquals(attempted, restorable)))
        {
            return (snapshot, after);
        }

        return (states.RoundTrip<TDoc>(restorable, record), before);
    }

    /// <summary>
    ///     Where a single-aggregate step-through gets the identity to fold each event under. A multi-stream
    ///     projection cannot work it out from an event without its slicer, so it takes the one the caller
    ///     named.
    /// </summary>
    private protected virtual Func<IEvent, TId> stepIdentitySource(object identity)
    {
        if (identity is TId id) return _ => id;

        throw new ArgumentException(
            $"Projection '{Name}' needs an identity of type '{typeof(TId).FullNameInCode()}' to fold these events under, " +
            $"not '{identity?.GetType().FullNameInCode() ?? "null"}'. RunMultiStreamProjectionAsync finds the identities " +
            "through the projection's own slicing.", nameof(identity));
    }
}
