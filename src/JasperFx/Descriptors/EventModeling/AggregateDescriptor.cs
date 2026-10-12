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
    IReadOnlyList<TypeDescriptor> AppliedEvents)
{
    /// <summary>
    /// Event types the aggregate consumes, in declaration order. Never null: System.Text.Json leaves
    /// a constructor parameter the JSON does not carry at its <c>default</c>, so a payload that omits
    /// the member would otherwise hand back a null list from a non-nullable slot (jasperfx#807).
    /// </summary>
    public IReadOnlyList<TypeDescriptor> AppliedEvents { get; init; } = AppliedEvents ?? Array.Empty<TypeDescriptor>();

    /// <summary>
    /// The aggregate's identity type — the type of its stream id, e.g. <c>OrderId</c>, <see cref="Guid"/> or
    /// <see cref="string"/> (jasperfx#1004). Null when no source has said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the aggregate's single-stream projection by <c>ProjectionEventModelSource</c> (the
    /// <c>TId</c> of <c>SingleStreamProjection&lt;TDoc, TId&gt;</c>), so a running system needs no
    /// declaration. Declared with <c>EventModelBuilder.Aggregate&lt;T&gt;().IdentifiedBy&lt;TId&gt;()</c> for
    /// design-first work before the code exists.
    /// </para>
    /// <para>
    /// Read by Wolverine's scaffold to write strong-typed ids, by Bobcat's generated specifications, and by
    /// consoles that display the aggregate. When two sources claim different identity types,
    /// <see cref="EventModelDescriptor.Merge"/> keeps the higher rung's and records a model-level
    /// <see cref="HotspotOrigin.SourceDisagreement"/> on <see cref="EventModelRole.AggregateIdentity"/>.
    /// </para>
    /// </remarks>
    public TypeDescriptor? IdentityType { get; init; }

    /// <summary>
    /// Which rung of the provenance ladder the source that described this aggregate sits on (jasperfx#1004).
    /// Null means unattributed, read as <see cref="EventModelProvenance.Declared"/>.
    /// </summary>
    /// <remarks>
    /// Stamped by <see cref="EventModelDescriptor.WithProvenance"/> alongside the slices. Used to decide
    /// <see cref="IdentityType"/> when two sources disagree; which descriptor <em>entry</em> survives a merge is
    /// unchanged (first wins, and a real type replaces a declaration). On a merged aggregate it is the higher
    /// of the two rungs.
    /// </remarks>
    public EventModelProvenance? Provenance { get; init; }
}
