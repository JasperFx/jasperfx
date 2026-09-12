using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#824 — the roles for what a slice <em>reads</em> (<c>ConsumedEvents</c>, <c>ReadsFrom</c>)
/// and the <c>Chapter</c> grouping.
/// </summary>
public class SliceReadRoleTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static TypeDescriptor Declared(string name) => new(name, $"Declared.{name}", string.Empty);

    private static string Id(string slice, EventModelElementKind kind, TypeDescriptor type)
        => EventModelElement.IdFor(slice, kind, type.FullName);

    /// <summary>
    /// A descriptor that declares none of the three is exactly what it was before — the compatibility
    /// hinge for every existing source and every stored payload.
    /// </summary>
    [Fact]
    public void a_slice_that_declares_none_of_the_three_is_unchanged()
    {
        var slice = EventModelSliceDescriptor.Named("x");

        slice.ConsumedEvents.ShouldBeEmpty();
        slice.ReadsFrom.ShouldBeEmpty();
        slice.Chapter.ShouldBeNull();

        slice.Claims(EventModelRole.ConsumedEvents).ShouldBeFalse();
        slice.Claims(EventModelRole.ReadsFrom).ShouldBeFalse();
        slice.Claims(EventModelRole.Chapter).ShouldBeFalse();

        slice.Elements.ShouldBeEmpty();
        slice.Edges.ShouldBeEmpty();
    }

    /// <summary>
    /// A consumed event becomes an <see cref="EventModelElementKind.Event" /> element in the consuming
    /// slice's own event-stream lane, and feeds the projection exactly as an emitted event would.
    /// </summary>
    /// <remarks>
    /// This is the "repeat the sticky where it is consumed" convention, and it is what makes a State
    /// View slice drawable at all: the event → projection → read model arrow is the one people most
    /// expect on an Event Modeling board, and nothing in the descriptor could say a projection applies
    /// an event.
    /// </remarks>
    [Fact]
    public void a_consumed_event_is_an_event_element_that_feeds_the_projection()
    {
        var view = EventModelSliceDescriptor.Named("AccountBalance") with
        {
            Pattern = SlicePattern.View,
            ConsumedEvents = [T<AccountOpened>()],
            ProjectionTypes = [T<BalanceProjection>()],
            ReadModelTypes = [T<AccountBalance>()],
        };

        var element = view.Elements.Where(x => x.Kind == EventModelElementKind.Event).ShouldHaveSingleItem();
        element.Lane.ShouldBe(EventModelLane.EventStream);
        element.Type.ShouldBe(T<AccountOpened>());

        view.Edges.ShouldBe(
        [
            new EventModelEdge(Id("AccountBalance", EventModelElementKind.Event, T<AccountOpened>()),
                Id("AccountBalance", EventModelElementKind.Projection, T<BalanceProjection>())),
            new EventModelEdge(Id("AccountBalance", EventModelElementKind.Projection, T<BalanceProjection>()),
                Id("AccountBalance", EventModelElementKind.ReadModel, T<AccountBalance>())),
        ]);
    }

    /// <summary>
    /// An event a slice both emits and consumes is <b>one</b> element.
    /// </summary>
    /// <remarks>
    /// Two elements would be two overlapping stickies at the same id, and every edge off that event
    /// would be drawn twice. The consumed entry is also not given a processor → event edge: the slice
    /// reads it, it did not write it.
    /// </remarks>
    [Fact]
    public void an_event_both_emitted_and_consumed_is_one_element()
    {
        var slice = EventModelSliceDescriptor.Named("Rebalance") with
        {
            HandlerType = T<RebalanceHandler>(),
            EmittedEvents = [T<AccountOpened>()],
            ConsumedEvents = [T<AccountOpened>()],
        };

        slice.Elements.Count(x => x.Kind == EventModelElementKind.Event).ShouldBe(1);

        // One processor → event edge, not two.
        slice.Edges.Count(x => x.ToId == Id("Rebalance", EventModelElementKind.Event, T<AccountOpened>()))
            .ShouldBe(1);
    }

    /// <summary>
    /// A read model the slice reads points <em>at</em> the processor — the opposite direction from one
    /// it produces.
    /// </summary>
    /// <remarks>
    /// The Automation pattern's input edge, Event → Read Model (todo list) → ⚙ Command, which could
    /// not be drawn before: <c>ReadModelTypes</c> meant "reads from <em>or</em> produces", Wolverine
    /// folded <c>[ReadModel]</c> parameters and <c>IStorageAction&lt;T&gt;</c> returns into it alike,
    /// and nothing ever linked a read model to a processor.
    /// </remarks>
    [Fact]
    public void a_read_model_the_slice_reads_points_at_the_processor()
    {
        var automation = EventModelSliceDescriptor.Named("ChaseOverdue") with
        {
            Pattern = SlicePattern.Automation,
            CommandType = T<ChaseOverdue>(),
            HandlerType = T<ChaseHandler>(),
            ReadsFrom = [T<OverdueList>()],
        };

        var element = automation.Elements.Where(x => x.Kind == EventModelElementKind.ReadModel).ShouldHaveSingleItem();
        element.Lane.ShouldBe(EventModelLane.ReadModel);

        automation.Edges.ShouldContain(new EventModelEdge(
            Id("ChaseOverdue", EventModelElementKind.ReadModel, T<OverdueList>()),
            Id("ChaseOverdue", EventModelElementKind.Command, T<ChaseOverdue>())));
    }

    /// <summary>
    /// With no processor at all — a UI read — the read points at the trigger instead.
    /// </summary>
    [Fact]
    public void with_no_processor_a_read_model_points_at_the_trigger()
    {
        var ui = EventModelSliceDescriptor.Named("Dashboard") with
        {
            TriggerLabel = "User opens the dashboard",
            ReadsFrom = [T<OverdueList>()],
        };

        ui.Edges.ShouldContain(new EventModelEdge(
            Id("Dashboard", EventModelElementKind.ReadModel, T<OverdueList>()),
            EventModelElement.IdFor("Dashboard", EventModelElementKind.Trigger, "User opens the dashboard")));
    }

    /// <summary>
    /// A read model both read and produced by one slice is one element, same rule as the events.
    /// </summary>
    [Fact]
    public void a_read_model_both_read_and_produced_is_one_element()
    {
        var slice = EventModelSliceDescriptor.Named("Recompute") with
        {
            HandlerType = T<ChaseHandler>(),
            ReadModelTypes = [T<OverdueList>()],
            ReadsFrom = [T<OverdueList>()],
        };

        slice.Elements.Count(x => x.Kind == EventModelElementKind.ReadModel).ShouldBe(1);
    }

    /// <summary>
    /// The elements a slice repeats carry the provenance of the role they came from, not of the role
    /// that shares their kind.
    /// </summary>
    /// <remarks>
    /// A consumed event is an <c>Event</c> element but it is not an <c>EmittedEvents</c> claim, and a
    /// read-from is a <c>ReadModel</c> element but not a <c>ReadModelTypes</c> claim. Getting this
    /// wrong would shade a derived consumption as though a declaration had made it.
    /// </remarks>
    [Fact]
    public void repeated_elements_carry_their_own_roles_provenance()
    {
        var slice = EventModelSliceDescriptor.Named("AccountBalance") with
        {
            ConsumedEvents = [T<AccountOpened>()],
            ReadsFrom = [T<OverdueList>()],
            Provenance = EventModelProvenance.Derived,
        };

        slice.ProvenanceFor(EventModelRole.ConsumedEvents).ShouldBe(EventModelProvenance.Derived);
        slice.ProvenanceFor(EventModelRole.ReadsFrom).ShouldBe(EventModelProvenance.Derived);

        // Nothing claims the roles these elements' KINDS map to.
        slice.ProvenanceFor(EventModelRole.EmittedEvents).ShouldBeNull();
        slice.ProvenanceFor(EventModelRole.ReadModelTypes).ShouldBeNull();

        slice.Elements.ShouldAllBe(x => x.Provenance == EventModelProvenance.Derived);
    }

    /// <summary>
    /// The two lists merge as every other type list does, including the declared-vs-derived identity
    /// fold.
    /// </summary>
    [Fact]
    public void the_two_lists_merge_by_the_usual_type_identity_rule()
    {
        var declared = EventModelSliceDescriptor.Named("AccountBalance") with
        {
            ConsumedEvents = [Declared(nameof(AccountOpened))],
            Provenance = EventModelProvenance.Declared,
        };

        var derived = EventModelSliceDescriptor.Named("AccountBalance") with
        {
            ConsumedEvents = [T<AccountOpened>()],
            Provenance = EventModelProvenance.Derived,
        };

        var merged = declared.Merge(derived);

        // The derived rung wins the role, and the two claims are recognised as the same type -- so
        // nothing is recorded as a disagreement.
        merged.ConsumedEvents.ShouldHaveSingleItem().ShouldBe(T<AccountOpened>());
        merged.ProvenanceFor(EventModelRole.ConsumedEvents).ShouldBe(EventModelProvenance.Derived);
        merged.Hotspots.ShouldBeEmpty();
    }

    /// <summary>
    /// <see cref="EventModelSliceDescriptor.Chapter" /> merges like <see cref="EventModelSliceDescriptor.Domain" />,
    /// and a genuine disagreement is a hotspot rather than a silent first-wins.
    /// </summary>
    [Fact]
    public void chapter_merges_like_domain_and_a_disagreement_is_a_hotspot()
    {
        var first = EventModelSliceDescriptor.Named("s") with { Chapter = "Onboarding" };
        var agreeing = EventModelSliceDescriptor.Named("s") with { Chapter = "Onboarding" };
        var disagreeing = EventModelSliceDescriptor.Named("s") with { Chapter = "Servicing" };

        // Same rung, same value: first wins and nothing is recorded.
        first.Merge(agreeing).Chapter.ShouldBe("Onboarding");
        first.Merge(agreeing).Hotspots.ShouldBeEmpty();

        // Same rung, different values: first still wins, and the loser is recorded rather than lost.
        var merged = first.Merge(disagreeing);
        merged.Chapter.ShouldBe("Onboarding");

        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
        hotspot.Text.ShouldContain("Servicing");
    }

    /// <summary>
    /// A slice that names no chapter never overrides one that does, whatever rung it sits on — the
    /// per-claimed-role rule that keeps declarations owning what only declarations claim.
    /// </summary>
    [Fact]
    public void a_slice_with_no_chapter_does_not_clear_one()
    {
        var declared = EventModelSliceDescriptor.Named("s") with
        {
            Chapter = "Onboarding",
            Provenance = EventModelProvenance.Declared,
        };

        var derived = EventModelSliceDescriptor.Named("s") with { Provenance = EventModelProvenance.Observed };

        declared.Merge(derived).Chapter.ShouldBe("Onboarding");
        derived.Merge(declared).Chapter.ShouldBe("Onboarding");
    }

    /// <summary>
    /// <see cref="EventModelLinkKind.EventConsumed" /> — the State View arrow, across two slices.
    /// </summary>
    [Fact]
    public void a_consumed_event_links_to_the_slice_that_emitted_it()
    {
        var open = EventModelSliceDescriptor.Named("OpenAccount") with { EmittedEvents = [T<AccountOpened>()] };
        var balance = EventModelSliceDescriptor.Named("AccountBalance") with
        {
            Pattern = SlicePattern.View,
            ConsumedEvents = [T<AccountOpened>()],
        };

        var link = new EventModelDescriptor("Bank", [open, balance]).Links.ShouldHaveSingleItem();

        link.Kind.ShouldBe(EventModelLinkKind.EventConsumed);
        link.FromSlice.ShouldBe("OpenAccount");
        link.ToSlice.ShouldBe("AccountBalance");

        open.Elements.Select(x => x.Id).ShouldContain(link.FromElementId);
        balance.Elements.Select(x => x.Id).ShouldContain(link.ToElementId);
    }

    /// <summary>
    /// <see cref="EventModelLinkKind.ReadModelRead" /> — the Automation's input, across two slices.
    /// </summary>
    [Fact]
    public void a_read_model_links_from_the_slice_that_produces_it()
    {
        var view = EventModelSliceDescriptor.Named("OverdueList") with
        {
            Pattern = SlicePattern.View,
            ReadModelTypes = [T<OverdueList>()],
        };

        var automation = EventModelSliceDescriptor.Named("ChaseOverdue") with
        {
            Pattern = SlicePattern.Automation,
            ReadsFrom = [T<OverdueList>()],
        };

        var link = new EventModelDescriptor("Bank", [view, automation]).Links.ShouldHaveSingleItem();

        link.Kind.ShouldBe(EventModelLinkKind.ReadModelRead);
        link.FromSlice.ShouldBe("OverdueList");
        link.ToSlice.ShouldBe("ChaseOverdue");
    }

    /// <summary>
    /// The two new kinds fold a declaration into the derived type it describes, same as the two that
    /// shipped with jasperfx#823.
    /// </summary>
    [Fact]
    public void the_new_link_kinds_match_a_declared_type_to_a_derived_one()
    {
        var open = EventModelSliceDescriptor.Named("OpenAccount") with { EmittedEvents = [T<AccountOpened>()] };
        var balance = EventModelSliceDescriptor.Named("AccountBalance") with
        {
            ConsumedEvents = [Declared(nameof(AccountOpened))],
        };

        new EventModelDescriptor("Bank", [open, balance])
            .Links.ShouldHaveSingleItem()
            .Kind.ShouldBe(EventModelLinkKind.EventConsumed);
    }

    public class AccountOpened { }
    public class AccountBalance { }
    public class BalanceProjection { }
    public class RebalanceHandler { }
    public class OverdueList { }
    public class ChaseOverdue { }
    public class ChaseHandler { }
}
