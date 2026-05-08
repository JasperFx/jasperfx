using System.Text.Json;

namespace JasperFx.Descriptors;

/// <summary>
/// Strong-typed result of running a projection over a fixed event list.
/// Returned by the event store explorer's projection-stepper view so
/// operators can walk the per-event before/after state.
/// </summary>
/// <typeparam name="TState">CLR type of the projected state.</typeparam>
/// <param name="Steps">Per-event step results in apply order.</param>
/// <param name="FinalState">State after the last successfully applied event, or <see langword="null"/> when no events produced state.</param>
public sealed record ProjectionTimeline<TState>(
    IReadOnlyList<ProjectionStepResult<TState>> Steps,
    TState? FinalState);

/// <summary>
/// Untyped variant of <see cref="ProjectionTimeline{TState}"/> used when
/// the projection's state type is referenced by name. State snapshots
/// are carried as JSON so monitoring tools can render them without
/// owning the projected CLR type.
/// </summary>
/// <param name="Steps">Per-event step results in apply order.</param>
/// <param name="FinalState">JSON state after the last successfully applied event, or <see langword="null"/> when no events produced state.</param>
public sealed record ProjectionTimelineRaw(
    IReadOnlyList<ProjectionStepResultRaw> Steps,
    JsonElement? FinalState);

/// <summary>
/// One row in a <see cref="ProjectionTimeline{TState}"/>: the event that
/// was applied, the state immediately before and after the apply, and
/// any error raised by the projection's apply method.
/// </summary>
/// <typeparam name="TState">CLR type of the projected state.</typeparam>
/// <param name="Event">Event that was fed into the projection on this step.</param>
/// <param name="Before">Projected state before this event was applied.</param>
/// <param name="After">Projected state after this event was applied; equal to <paramref name="Before"/> when the apply threw.</param>
/// <param name="Elapsed">Wall-clock time spent inside the apply method for this event.</param>
/// <param name="Error">Exception thrown by the projection's apply method, or <see langword="null"/> when the apply succeeded.</param>
public sealed record ProjectionStepResult<TState>(
    EventRecord Event,
    TState? Before,
    TState? After,
    TimeSpan Elapsed,
    Exception? Error);

/// <summary>
/// Untyped variant of <see cref="ProjectionStepResult{TState}"/>. State
/// snapshots are carried as JSON and the error is reduced to its message
/// so the record can be safely round-tripped over the wire.
/// </summary>
/// <param name="Event">Event that was fed into the projection on this step.</param>
/// <param name="Before">JSON state before this event was applied.</param>
/// <param name="After">JSON state after this event was applied; equal to <paramref name="Before"/> when the apply threw.</param>
/// <param name="Elapsed">Wall-clock time spent inside the apply method for this event.</param>
/// <param name="Error">Message of the exception thrown by the projection's apply method, or <see langword="null"/> when the apply succeeded.</param>
public sealed record ProjectionStepResultRaw(
    EventRecord Event,
    JsonElement? Before,
    JsonElement? After,
    TimeSpan Elapsed,
    string? Error);

/// <summary>
/// Aggregated result when a single event list fans out across multiple
/// aggregate identities (e.g. a multi-stream projection). Each entry is
/// the timeline produced for one aggregate identity.
/// </summary>
/// <param name="ProjectionName">Name of the projection that produced these timelines.</param>
/// <param name="AggregatesByIdentity">Per-aggregate timelines keyed by string form of the aggregate identity.</param>
public sealed record MultiAggregateProjectionResult(
    string ProjectionName,
    IReadOnlyDictionary<string, ProjectionTimelineRaw> AggregatesByIdentity);
