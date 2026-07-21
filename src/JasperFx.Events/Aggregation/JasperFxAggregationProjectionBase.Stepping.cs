using System.Diagnostics;
using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;

namespace JasperFx.Events.Aggregation;

public abstract partial class JasperFxAggregationProjectionBase<TDoc, TId, TOperations, TQuerySession>
    : ISteppableAggregation<TQuerySession>
    where TOperations : TQuerySession, IStorageOperations where TDoc : notnull where TId : notnull
{
    /// <inheritdoc />
    public async Task<MultiAggregateProjectionResult> BuildTimelinesAsync(
        IReadOnlyList<IEvent> events,
        TQuerySession session,
        Func<object?, JsonElement?> serialize,
        Func<IEvent, EventRecord> toRecord,
        IProjectionStepObserver? observer,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(serialize);
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
                var timeline = await foldSliceAsync(slice, session, serialize, toRecord, observer, cancellation)
                    .ConfigureAwait(false);

                // A multi-stream slicer never produces two slices for the same identity within
                // a group, but guard anyway so a duplicate can't throw and lose the whole run.
                byIdentity[identity] = timeline;
            }
        }

        return new MultiAggregateProjectionResult(Name, byIdentity);
    }

    private async Task<ProjectionTimelineRaw> foldSliceAsync(
        EventSlice<TDoc, TId> slice,
        TQuerySession session,
        Func<object?, JsonElement?> serialize,
        Func<IEvent, EventRecord> toRecord,
        IProjectionStepObserver? observer,
        CancellationToken cancellation)
    {
        var identity = slice.Id?.ToString() ?? string.Empty;
        var steps = new List<ProjectionStepResultRaw>();
        var identitySetter = new NulloIdentitySetter<TDoc, TId>();

        TDoc? snapshot = default;

        foreach (var @event in slice.Events())
        {
            cancellation.ThrowIfCancellationRequested();

            var before = serialize(snapshot);
            string? error = null;

            var start = Stopwatch.GetTimestamp();
            try
            {
                // One event at a time so we can capture the state between each apply.
                // DetermineActionAsync is the real dispatch used by the daemon — it routes
                // to the projection's Create/Apply/ShouldDelete conventions (or an Evolve/
                // DetermineAction override, or the source-generated evolver) and reports a
                // delete via ActionType, so this honors user logic exactly rather than a
                // reflection re-implementation.
                var (result, action) = await DetermineActionAsync(
                    session, snapshot, slice.Id, identitySetter, [@event], cancellation).ConfigureAwait(false);

                snapshot = action == ActionType.Delete ? default : result;
            }
            catch (Exception e)
            {
                // Capture the failure on the step and keep the pre-apply snapshot so the
                // remaining events still fold — the stepper wants to show the whole timeline,
                // including the event that blew up, not stop at the first error. Unwrap the
                // ApplyEventException the fold path adds so the step carries the projection's
                // own apply-method message rather than the daemon's "Failure to apply event #n".
                error = e is ApplyEventException { InnerException: { } inner } ? inner.Message : e.Message;
            }

            var elapsed = Stopwatch.GetElapsedTime(start);
            var after = serialize(snapshot);

            var step = new ProjectionStepResultRaw(toRecord(@event), before, after, elapsed, error);
            steps.Add(step);

            if (observer != null)
            {
                await observer.ObserveAsync(identity, step, cancellation).ConfigureAwait(false);
            }
        }

        return new ProjectionTimelineRaw(steps, serialize(snapshot));
    }
}
