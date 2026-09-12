using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#823 — the cross-slice cause→effect links, computed on read from the roles every slice
/// already stamps.
/// </summary>
public class EventModelLinkTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    /// <summary>A type identity with no assembly — what a declaration looks like on the wire.</summary>
    /// <remarks>
    /// The empty <see cref="TypeDescriptor.AssemblyName" /> is the marker, and the <c>FullName</c> is
    /// deliberately a plausible-but-wrong guess: Bobcat's curated mapper builds
    /// <c>{namespace}.{Name}</c> from the model's single namespace, and real code puts types in
    /// whatever namespaces it likes. A join keyed on that guess is a type disagreeing with itself.
    /// </remarks>
    private static TypeDescriptor Declared(string name, string @namespace = "Declared")
        => new(name, $"{@namespace}.{name}", string.Empty);

    private static EventModelDescriptor model(params EventModelSliceDescriptor[] slices)
        => new("Orders", slices);

    private static EventModelSliceDescriptor emits(string name, params TypeDescriptor[] events)
        => EventModelSliceDescriptor.Named(name) with { EmittedEvents = events };

    /// <summary>
    /// The join that exists today: a slice whose command is another slice's emitted event.
    /// </summary>
    /// <remarks>
    /// This is the rule Wolverine's <c>FinishModel</c> already applies to re-pattern a slice as
    /// <see cref="SlicePattern.Automation" /> — but it produced a <em>pattern</em> and kept a private
    /// copy of the rule. Here it produces an edge, from the emitted-event element in the cause to the
    /// command element in the effect, which is what a canvas draws and what "what consumes
    /// OrderPlaced?" answers.
    /// </remarks>
    [Fact]
    public void an_emitted_event_that_is_another_slices_command_is_an_event_triggers_link()
    {
        var placed = emits("PlaceOrder", T<OrderPlaced>());
        var reserve = EventModelSliceDescriptor.Named("ReserveStock") with { CommandType = T<OrderPlaced>() };

        var link = model(placed, reserve).Links.ShouldHaveSingleItem();

        link.Kind.ShouldBe(EventModelLinkKind.EventTriggers);
        link.FromSlice.ShouldBe("PlaceOrder");
        link.ToSlice.ShouldBe("ReserveStock");
        link.Via.FullName.ShouldBe(typeof(OrderPlaced).FullName);

        // Both ends are element ids the slices themselves report, so a consumer that has laid the
        // elements out can draw the link with no further resolution. That is the whole contract.
        placed.Elements.Select(x => x.Id).ShouldContain(link.FromElementId);
        reserve.Elements.Select(x => x.Id).ShouldContain(link.ToElementId);
    }

    /// <summary>
    /// The same join through <see cref="EventModelSliceDescriptor.TriggerType" />, which is how a
    /// declared "when this happens" slice spells the same relationship.
    /// </summary>
    [Fact]
    public void a_trigger_type_matching_an_emitted_event_links_to_the_trigger_element()
    {
        var placed = emits("PlaceOrder", T<OrderPlaced>());
        var notify = EventModelSliceDescriptor.Named("NotifyCustomer") with { TriggerType = T<OrderPlaced>() };

        var link = model(placed, notify).Links.ShouldHaveSingleItem();

        link.Kind.ShouldBe(EventModelLinkKind.EventTriggers);
        link.ToElementId.ShouldBe(
            EventModelElement.IdFor("NotifyCustomer", EventModelElementKind.Trigger, typeof(OrderPlaced).FullName!));
    }

    /// <summary>
    /// A published message handled by another slice is a <see cref="EventModelLinkKind.MessageTriggers" />
    /// link, not an event one.
    /// </summary>
    /// <remarks>
    /// Kept apart from events because a message is not an event: the two render differently, and
    /// <see cref="EventModelSliceDescriptor.PublishedMessages" /> exists precisely so the event stream
    /// lane shows only events.
    /// </remarks>
    [Fact]
    public void a_published_message_handled_elsewhere_is_a_message_triggers_link()
    {
        var placed = EventModelSliceDescriptor.Named("PlaceOrder") with { PublishedMessages = [T<NotifyWarehouse>()] };
        var warehouse = EventModelSliceDescriptor.Named("Warehouse") with { CommandType = T<NotifyWarehouse>() };

        var link = model(placed, warehouse).Links.ShouldHaveSingleItem();

        link.Kind.ShouldBe(EventModelLinkKind.MessageTriggers);
        link.FromElementId.ShouldBe(
            EventModelElement.IdFor("PlaceOrder", EventModelElementKind.Message, typeof(NotifyWarehouse).FullName!));
    }

    /// <summary>
    /// A slice never links to itself.
    /// </summary>
    /// <remarks>
    /// A slice that emits an event and handles it is expressible, and its internal relationship is
    /// already a slice-local <see cref="EventModelSliceDescriptor.Edges" /> entry. A cross-slice link
    /// on top of that would draw an arrow from a sticky back to the slice it sits in.
    /// </remarks>
    [Fact]
    public void a_slice_does_not_link_to_itself()
    {
        var reentrant = EventModelSliceDescriptor.Named("Loop") with
        {
            CommandType = T<OrderPlaced>(),
            EmittedEvents = [T<OrderPlaced>()],
        };

        model(reentrant).Links.ShouldBeEmpty();

        // And still not when a second, unrelated slice makes the model big enough to have pairs.
        model(reentrant, EventModelSliceDescriptor.Named("Other")).Links.ShouldBeEmpty();
    }

    /// <summary>
    /// <b>The identity rule, and the reason it is not simply <c>FullName</c>.</b> A declared type in
    /// one slice matches a derived type in another by short name, exactly as
    /// <see cref="EventModelSliceDescriptor.Merge" /> folds them.
    /// </summary>
    /// <remarks>
    /// Without this, a spec-declared slice and the code that implements it stay unconnected on the
    /// canvas — which is precisely the pairing the two provenance rungs exist to make. The declared
    /// side's <c>FullName</c> is a synthesized guess (jasperfx#798), so keying on it compares a real
    /// namespace against an invented one.
    /// </remarks>
    [Fact]
    public void a_declared_type_matches_a_derived_type_by_short_name()
    {
        // The declaration guesses "Declared.OrderPlaced"; the real type is in EventTests.EventModeling.
        var declared = emits("PlaceOrder", Declared(nameof(OrderPlaced)));
        var derived = EventModelSliceDescriptor.Named("ReserveStock") with { CommandType = T<OrderPlaced>() };

        var link = model(declared, derived).Links.ShouldHaveSingleItem();

        link.Kind.ShouldBe(EventModelLinkKind.EventTriggers);

        // Via is the CAUSE's descriptor, so the element id it names is one the cause actually reports.
        link.Via.FullName.ShouldBe("Declared.OrderPlaced");
        declared.Elements.Select(x => x.Id).ShouldContain(link.FromElementId);
    }

    /// <summary>
    /// Two real types that share a short name and differ in namespace are <em>not</em> the same type.
    /// </summary>
    /// <remarks>
    /// The other half of the identity rule, and the one that keeps it from being a blunt name match:
    /// short-name keying applies only when one side is a declaration. Two derived types both carry a
    /// real assembly name, so the full name decides.
    /// </remarks>
    [Fact]
    public void two_derived_types_with_the_same_short_name_in_different_namespaces_do_not_match()
    {
        var placed = emits("PlaceOrder", T<OrderPlaced>());
        var other = EventModelSliceDescriptor.Named("Elsewhere") with { CommandType = T<Shipping.OrderPlaced>() };

        model(placed, other).Links.ShouldBeEmpty();
    }

    /// <summary>
    /// Order is deterministic, so a serialized document is byte-stable and a diff of two exports shows
    /// only what changed.
    /// </summary>
    /// <remarks>
    /// Pinned with a model whose slices are declared in an order that does <em>not</em> match any of
    /// the sort keys' natural order, so a nested loop that happened to produce the right answer by
    /// construction cannot pass this by accident.
    /// </remarks>
    [Fact]
    public void links_are_ordered_by_from_slice_then_to_slice_then_kind_then_type()
    {
        var source = EventModelSliceDescriptor.Named("Source") with
        {
            EmittedEvents = [T<OrderConfirmed>(), T<OrderPlaced>()],
            PublishedMessages = [T<NotifyWarehouse>()],
        };

        // Declared last, links first: FromSlice order is declaration order, not alphabetical.
        var zulu = EventModelSliceDescriptor.Named("Zulu") with { CommandType = T<OrderPlaced>() };
        var alpha = EventModelSliceDescriptor.Named("Alpha") with
        {
            CommandType = T<NotifyWarehouse>(),
            TriggerType = T<OrderConfirmed>(),
        };

        var links = new EventModelDescriptor("Orders", [source, zulu, alpha]).Links;

        links.Select(x => (x.ToSlice, x.Kind, x.Via.Name)).ShouldBe(
        [
            // Zulu is declared before Alpha, so every Zulu link comes first.
            ("Zulu", EventModelLinkKind.EventTriggers, nameof(OrderPlaced)),
            // Within Alpha: EventTriggers (0) before MessageTriggers (1).
            ("Alpha", EventModelLinkKind.EventTriggers, nameof(OrderConfirmed)),
            ("Alpha", EventModelLinkKind.MessageTriggers, nameof(NotifyWarehouse)),
        ]);
    }

    /// <summary>
    /// The join is reachable as a public pure function, which is the point of the issue as much as the
    /// property is: Wolverine re-bases its Automation reclassification on this instead of keeping a
    /// second copy of the rule.
    /// </summary>
    [Fact]
    public void the_join_is_a_public_pure_function_over_a_descriptor()
    {
        var descriptor = model(emits("PlaceOrder", T<OrderPlaced>()),
            EventModelSliceDescriptor.Named("ReserveStock") with { CommandType = T<OrderPlaced>() });

        EventModelLinks.Compute(descriptor).ShouldBe(descriptor.Links);
    }

    [Fact]
    public void a_model_with_fewer_than_two_slices_has_no_links()
    {
        new EventModelDescriptor("Orders", []).Links.ShouldBeEmpty();
        model(emits("PlaceOrder", T<OrderPlaced>())).Links.ShouldBeEmpty();
    }

    /// <summary>
    /// Links are recomputed over the merged slices rather than merged themselves — so a relationship
    /// that only exists once two sources' halves are folded together still appears.
    /// </summary>
    /// <remarks>
    /// This is the payoff of computing on read. One source knows the slice emits <c>OrderPlaced</c>,
    /// another knows a second slice handles it; neither source's own descriptor carries the link, and
    /// the assembled model does.
    /// </remarks>
    [Fact]
    public void links_appear_across_a_merge_of_two_sources_that_each_saw_half()
    {
        var fromEvents = model(emits("PlaceOrder", T<OrderPlaced>()), EventModelSliceDescriptor.Named("ReserveStock"));
        var fromHandlers = model(EventModelSliceDescriptor.Named("PlaceOrder"),
            EventModelSliceDescriptor.Named("ReserveStock") with { CommandType = T<OrderPlaced>() });

        fromEvents.Links.ShouldBeEmpty();
        fromHandlers.Links.ShouldBeEmpty();

        EventModelDescriptor.Merge("Orders", [fromEvents, fromHandlers])
            .Links.ShouldHaveSingleItem()
            .Kind.ShouldBe(EventModelLinkKind.EventTriggers);
    }

    public class OrderPlaced { }
    public class OrderConfirmed { }
    public class NotifyWarehouse { }

    public static class Shipping
    {
        public class OrderPlaced { }
    }
}
