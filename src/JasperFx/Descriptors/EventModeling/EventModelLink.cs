using JasperFx.Descriptors;

namespace JasperFx.Events.EventModeling;

/// <summary>
/// What kind of cause→effect relationship an <see cref="EventModelLink"/> records between two
/// slices (jasperfx#823).
/// </summary>
/// <remarks>
/// All four members are declared together, deliberately, even though the last two produce nothing
/// until a slice carries the roles jasperfx#824 adds. This is a wire enum read by consoles, the MCP
/// event-model tools and Stoat; moving it twice costs every one of them a release.
/// </remarks>
public enum EventModelLinkKind
{
    /// <summary>
    /// Slice A emits event E; slice B is triggered by E — B's <c>CommandType</c> or
    /// <c>TriggerType</c> <em>is</em> that event. This is the Automation / Translation arrow: the
    /// thing that happens because something else happened.
    /// </summary>
    EventTriggers,

    /// <summary>
    /// Slice A publishes message M; slice B handles M. The cascading-message arrow — same shape as
    /// <see cref="EventTriggers"/>, kept apart because a message is not an event and the two render
    /// differently.
    /// </summary>
    MessageTriggers,

    /// <summary>
    /// Slice A emits event E; slice B <em>consumes</em> it —
    /// <see cref="EventModelSliceDescriptor.ConsumedEvents"/> contains E. The State View arrow, and
    /// the one people most expect on an Event Modeling board.
    /// </summary>
    EventConsumed,

    /// <summary>
    /// Slice A produces read model R; slice B reads it before deciding —
    /// <see cref="EventModelSliceDescriptor.ReadsFrom"/> contains R. The Automation pattern's input
    /// edge: Event → Read Model → ⚙ Command.
    /// </summary>
    ReadModelRead,
}

/// <summary>
/// One directed cause→effect relationship <em>between</em> two slices of an
/// <see cref="EventModelDescriptor"/> (jasperfx#823).
/// </summary>
/// <remarks>
/// <para>
/// Every edge in the vocabulary before this was slice-local:
/// <see cref="EventModelSliceDescriptor.Edges"/> joins elements inside one slice, element ids are
/// <c>{slice}/{kind}/{type}</c>, so the same event type in two slices is two ids and nothing said how
/// the slices related. The one cross-slice join in the stack lived in Wolverine's <c>FinishModel</c>,
/// which re-patterned a slice as <see cref="SlicePattern.Automation"/> when its command was another
/// slice's emitted event — a Wolverine-private copy of a rule that belongs to the model, and one that
/// produced a <em>pattern</em> rather than an edge.
/// </para>
/// <para>
/// Both ends are <see cref="EventModelElement.Id"/> values, so a consumer that has already laid out
/// the per-slice elements can draw a link with no further resolution. <see cref="Via"/> is the type
/// identity the join matched on, which is what a "what consumes <c>OrderPlaced</c>?" query needs and
/// what a renderer labels the arrow with.
/// </para>
/// </remarks>
/// <param name="FromSlice">Name of the slice the relationship starts in — the cause.</param>
/// <param name="FromElementId">Id of the element in <paramref name="FromSlice"/>, as that slice's <see cref="EventModelSliceDescriptor.Elements"/> reports it.</param>
/// <param name="ToSlice">Name of the slice the relationship lands in — the effect.</param>
/// <param name="ToElementId">Id of the element in <paramref name="ToSlice"/>.</param>
/// <param name="Kind">Which relationship this is.</param>
/// <param name="Via">The type identity the join matched on.</param>
public sealed record EventModelLink(
    string FromSlice,
    string FromElementId,
    string ToSlice,
    string ToElementId,
    EventModelLinkKind Kind,
    TypeDescriptor Via);

/// <summary>
/// The cross-slice join, as a public pure function (jasperfx#823).
/// </summary>
/// <remarks>
/// <para>
/// Public and pure on purpose: Wolverine's <c>FinishModel</c> re-patterns a slice as
/// <see cref="SlicePattern.Automation"/> on exactly this rule, and until now kept its own copy of it.
/// One rule, computed in one place, is the same discipline
/// <see cref="EventModelSliceDescriptor.Elements"/> already applies one level down — only roles are
/// stamped, the graph is computed on read, so there is one opinion about it.
/// </para>
/// <para>
/// <b>Type identity follows <c>Merge</c>'s rule</b>, and it has to: a link that did not fold a
/// declaration into the derived type it describes would leave a spec-declared slice unconnected to
/// the code that implements it, which is the whole reason the two rungs are merged by name in the
/// first place. Key on <see cref="TypeDescriptor.FullName"/> when <em>both</em> sides carry a
/// non-empty <see cref="TypeDescriptor.AssemblyName"/>, else on the short
/// <see cref="TypeDescriptor.Name"/> — an empty assembly name is exactly the marker for "this is a
/// declaration whose <c>FullName</c> is a synthesized guess" (jasperfx#798).
/// </para>
/// </remarks>
public static class EventModelLinks
{
    /// <summary>
    /// Every cross-slice link in <paramref name="model"/>, in a deterministic order.
    /// </summary>
    /// <remarks>
    /// Ordered by <c>FromSlice</c> declaration order, then <c>ToSlice</c> declaration order, then
    /// <see cref="EventModelLinkKind"/>, then <see cref="TypeDescriptor.FullName"/> of
    /// <see cref="EventModelLink.Via"/> — so a serialized document is byte-stable across runs and a
    /// diff of two exports shows only what changed.
    /// </remarks>
    public static IReadOnlyList<EventModelLink> Compute(EventModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var slices = model.Slices;
        if (slices.Count < 2) return Array.Empty<EventModelLink>();

        var links = new List<(int FromIndex, int ToIndex, EventModelLink Link)>();

        for (var from = 0; from < slices.Count; from++)
        {
            var cause = slices[from];

            for (var to = 0; to < slices.Count; to++)
            {
                // A slice never links to itself. Its own emitted-event → projection edge is already a
                // slice-local Edge, and a self-link would draw an arrow from a sticky to itself.
                if (from == to) continue;

                var effect = slices[to];

                foreach (var link in between(cause, effect))
                {
                    links.Add((from, to, link));
                }
            }
        }

        return links
            .OrderBy(x => x.FromIndex)
            .ThenBy(x => x.ToIndex)
            .ThenBy(x => (int)x.Link.Kind)
            .ThenBy(x => x.Link.Via.FullName, StringComparer.Ordinal)
            .Select(x => x.Link)
            .ToList();
    }

    private static IEnumerable<EventModelLink> between(
        EventModelSliceDescriptor cause, EventModelSliceDescriptor effect)
    {
        // The two trigger ends of the effect slice. A slice triggered by an event carries it as its
        // CommandType (a Wolverine handler whose message is an event) or as its TriggerType (a
        // declared "when this happens" arrow); both are the same relationship from the cause's side.
        var entries = new List<(TypeDescriptor Type, EventModelElementKind Kind)>();
        if (effect.CommandType is { } command) entries.Add((command, EventModelElementKind.Command));
        if (effect.TriggerType is { } trigger) entries.Add((trigger, EventModelElementKind.Trigger));

        foreach (var (type, kind) in entries)
        {
            foreach (var emitted in cause.EmittedEvents)
            {
                if (!sameType(emitted, type)) continue;

                yield return new EventModelLink(
                    cause.Name, EventModelElement.IdFor(cause.Name, EventModelElementKind.Event, emitted.FullName),
                    effect.Name, EventModelElement.IdFor(effect.Name, kind, type.FullName),
                    EventModelLinkKind.EventTriggers, emitted);
            }

            foreach (var published in cause.PublishedMessages)
            {
                if (!sameType(published, type)) continue;

                yield return new EventModelLink(
                    cause.Name, EventModelElement.IdFor(cause.Name, EventModelElementKind.Message, published.FullName),
                    effect.Name, EventModelElement.IdFor(effect.Name, kind, type.FullName),
                    EventModelLinkKind.MessageTriggers, published);
            }
        }

        // jasperfx#824's two kinds. The From end is the cause's own element; the To end is the
        // element the consuming slice repeats in its own lane, which is why ConsumedEvents and
        // ReadsFrom become elements of the consuming slice at all.
        foreach (var consumed in effect.ConsumedEvents)
        {
            foreach (var emitted in cause.EmittedEvents)
            {
                if (!sameType(emitted, consumed)) continue;

                yield return new EventModelLink(
                    cause.Name, EventModelElement.IdFor(cause.Name, EventModelElementKind.Event, emitted.FullName),
                    effect.Name, EventModelElement.IdFor(effect.Name, EventModelElementKind.Event, consumed.FullName),
                    EventModelLinkKind.EventConsumed, emitted);
            }
        }

        foreach (var read in effect.ReadsFrom)
        {
            foreach (var produced in cause.ReadModelTypes)
            {
                if (!sameType(produced, read)) continue;

                yield return new EventModelLink(
                    cause.Name, EventModelElement.IdFor(cause.Name, EventModelElementKind.ReadModel, produced.FullName),
                    effect.Name, EventModelElement.IdFor(effect.Name, EventModelElementKind.ReadModel, read.FullName),
                    EventModelLinkKind.ReadModelRead, produced);
            }
        }
    }

    /// <summary>
    /// Do these two descriptors name the same type? <see cref="EventModelSliceDescriptor.Merge"/>'s
    /// rule, applied per comparison rather than per list.
    /// </summary>
    private static bool sameType(TypeDescriptor left, TypeDescriptor right)
        => string.IsNullOrEmpty(left.AssemblyName) || string.IsNullOrEmpty(right.AssemblyName)
            ? string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            : string.Equals(left.FullName, right.FullName, StringComparison.Ordinal);
}
