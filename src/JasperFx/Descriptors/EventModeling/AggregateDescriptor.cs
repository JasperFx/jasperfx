using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// Kind discriminator for <see cref="AggregateDescriptor"/>. Mirrors the four
/// aggregate-marker attributes the CritterWatch source generator scans for
/// (per CritterWatch#143 / #144).
/// </summary>
public enum AggregateKind
{
    /// <summary>
    /// Type carries <c>[WriteAggregate]</c> — a Wolverine aggregate-handler
    /// target. Commands handled by such a type produce events that the
    /// aggregate also applies.
    /// </summary>
    WriteAggregate,

    /// <summary>
    /// Type carries <c>[ReadAggregate]</c> — Wolverine loads the current
    /// aggregate snapshot before the handler runs but doesn't manage event
    /// persistence on the way out.
    /// </summary>
    ReadAggregate,

    /// <summary>
    /// Type carries <c>[ConsistentAggregate]</c> — a write-aggregate
    /// variant that requires the "always enforce consistency" flag, raising
    /// concurrency exceptions on stale versions.
    /// </summary>
    ConsistentAggregate,

    /// <summary>
    /// Type carries <c>[BoundaryModel]</c> — a read model that crosses a
    /// service / bounded-context boundary, surfaced separately on the
    /// Event Modeling swim-lane.
    /// </summary>
    BoundaryModel,
}

/// <summary>
/// Diagnostic descriptor for an aggregate-shaped type that the CritterWatch
/// source generator (CritterWatch#144) finds in the consuming application.
/// One descriptor per CLR type annotated with one of the four aggregate-
/// marker attributes; emitted into the per-project
/// <c>CritterWatchAppManifest</c> as <c>static readonly</c> data and merged
/// at runtime by <c>Wolverine.CritterWatch</c> into the unified
/// <see cref="EventModelDescriptor"/> the swim-lane consumes.
/// </summary>
/// <param name="Type">CLR type carrying the aggregate marker.</param>
/// <param name="Kind">Which of the four marker attributes the type carries.</param>
/// <param name="AppliedEvents">
///     Event types the aggregate consumes via its <c>Apply</c> / <c>When</c>
///     methods (write/consistent kinds) or that the read model is keyed off
///     (read/boundary kinds), in declaration order. Empty when the
///     generator can't statically resolve the apply set.
/// </param>
public sealed record AggregateDescriptor(
    TypeDescriptor Type,
    AggregateKind Kind,
    IReadOnlyList<TypeDescriptor> AppliedEvents);
