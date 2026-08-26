using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#704: a source disagreement is a hotspot, not a swallowed merge.
/// </summary>
public class SourceDisagreementHotspotTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static EventModelSliceDescriptor Slice(EventModelProvenance provenance) =>
        EventModelSliceDescriptor.Named("PlaceOrder") with { Provenance = provenance };

    private static IReadOnlyList<HotspotDescriptor> DisagreementsIn(EventModelSliceDescriptor slice) =>
        slice.Hotspots.Where(x => x.Origin == HotspotOrigin.SourceDisagreement).ToList();

    #region the acceptance criterion

    [Fact]
    public void production_wins_and_the_losing_claim_becomes_a_hotspot()
    {
        // The issue's acceptance scenario, verbatim: a derived slice claiming events {A} merged with
        // an observed slice claiming {A, B}.
        var derived = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] };
        var observed = Slice(EventModelProvenance.Observed) with
        {
            EmittedEvents = [T<OrderPlaced>(), T<AuditRecorded>()],
        };

        var merged = derived.Merge(observed);

        // Production wins the roles...
        merged.EmittedEvents.Select(x => x.Name).ShouldBe(["OrderPlaced", "AuditRecorded"]);

        // ...and the claim that lost is recorded rather than dropped.
        var hotspot = DisagreementsIn(merged).ShouldHaveSingleItem();

        hotspot.Role.ShouldBe(EventModelRole.EmittedEvents);
        hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Observed, "OrderPlaced, AuditRecorded"));
        hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "OrderPlaced"));

        // Enough for a reader to see which source to trust and which to fix, without unpacking the
        // structured form.
        hotspot.Text.ShouldBe(
            "EmittedEvents: Observed claims OrderPlaced, AuditRecorded; Derived claims OrderPlaced");
    }

    [Fact]
    public void a_disagreement_renders_in_the_canonical_hotspot_colour()
    {
        var merged = (Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] })
            .Merge(Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<AuditRecorded>()] });

        var element = merged.Elements.Single(x => x.Kind == EventModelElementKind.Hotspot);

        // New semantics on shipped machinery: HotspotDescriptor already renders in both viewers, so
        // there is nothing new to draw.
        element.Lane.ShouldBe(EventModelLane.Wireframe);
        EventModelPalette.ColorFor(element.Kind).ShouldBe("#E91E63");
        element.Label.ShouldContain("EmittedEvents");
    }

    #endregion

    #region what counts as a disagreement

    [Fact]
    public void a_scalar_dropped_by_first_wins_is_a_disagreement_even_at_the_same_rung()
    {
        // Two sources on the same rung claiming different handlers: the ladder cannot separate them,
        // so first-wins drops one -- which is exactly the silent loss this issue is about.
        var first = Slice(EventModelProvenance.Derived) with { HandlerType = T<OrderHandler>() };
        var second = Slice(EventModelProvenance.Derived) with { HandlerType = T<LegacyOrderHandler>() };

        var merged = first.Merge(second);

        merged.HandlerType!.Name.ShouldBe("OrderHandler");

        var hotspot = DisagreementsIn(merged).ShouldHaveSingleItem();
        hotspot.Role.ShouldBe(EventModelRole.HandlerType);
        hotspot.WinningClaim!.Value.ShouldBe("OrderHandler");
        hotspot.LosingClaim!.Value.ShouldBe("LegacyOrderHandler");
    }

    [Fact]
    public void lists_at_the_same_rung_union_and_record_nothing()
    {
        // Nothing was lost -- the union kept both claims -- so there is nothing to disagree about.
        var first = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] };
        var second = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<AuditRecorded>()] };

        var merged = first.Merge(second);

        merged.EmittedEvents.Count.ShouldBe(2);
        DisagreementsIn(merged).ShouldBeEmpty();
    }

    [Fact]
    public void identical_claims_at_different_rungs_record_nothing()
    {
        // The code says it emits OrderPlaced and production agrees. That is the happy case, and it is
        // silent -- CritterWatch's "confirmed" expressed as the absence of a finding.
        var derived = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] };
        var observed = Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<OrderPlaced>()] };

        DisagreementsIn(derived.Merge(observed)).ShouldBeEmpty();
    }

    [Fact]
    public void list_order_alone_is_not_a_disagreement()
    {
        var derived = Slice(EventModelProvenance.Derived) with
        {
            EmittedEvents = [T<OrderPlaced>(), T<AuditRecorded>()],
        };
        var observed = Slice(EventModelProvenance.Observed) with
        {
            EmittedEvents = [T<AuditRecorded>(), T<OrderPlaced>()],
        };

        DisagreementsIn(derived.Merge(observed)).ShouldBeEmpty();
    }

    [Fact]
    public void a_role_only_one_source_claims_is_not_a_disagreement()
    {
        // Production has no opinion about the domain, so it is not disagreeing with the overlay --
        // it simply never spoke. This is the per-claimed-role rule doing its job.
        var declared = Slice(EventModelProvenance.Declared) with { Domain = "Ordering" };
        var observed = Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<OrderPlaced>()] };

        var merged = declared.Merge(observed);

        merged.Domain.ShouldBe("Ordering");
        DisagreementsIn(merged).ShouldBeEmpty();
    }

    [Fact]
    public void a_model_with_no_disagreements_is_unchanged()
    {
        // Deliberately additive: complementary sources produce exactly what they always did.
        var declared = Slice(EventModelProvenance.Declared) with { Domain = "Ordering", TriggerLabel = "Place Order" };
        var derived = Slice(EventModelProvenance.Derived) with
        {
            CommandType = T<PlaceOrder>(), EmittedEvents = [T<OrderPlaced>()],
        };

        declared.Merge(derived).Hotspots.ShouldBeEmpty();
    }

    #endregion

    #region several sources

    [Fact]
    public void every_pair_that_disagrees_is_recorded()
    {
        // Merges are pairwise, so three sources disagreeing about one role leave two findings, each
        // naming the two claims that actually met.
        var declared = Slice(EventModelProvenance.Declared) with { EmittedEvents = [T<OrderPlaced>()] };
        var derived = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderShipped>()] };
        var observed = Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<AuditRecorded>()] };

        var merged = declared.Merge(derived).Merge(observed);

        merged.EmittedEvents.ShouldHaveSingleItem().Name.ShouldBe("AuditRecorded");

        DisagreementsIn(merged).Select(x => x.Text).ShouldBe([
            "EmittedEvents: Derived claims OrderShipped; Declared claims OrderPlaced",
            "EmittedEvents: Observed claims AuditRecorded; Derived claims OrderShipped",
        ]);
    }

    [Fact]
    public void an_earlier_finding_survives_a_later_merge_against_a_higher_rung()
    {
        // Hotspots are annotations, not claims, so they union rather than being arbitrated. Letting a
        // higher-rung source's hotspot list replace a lower one would discard exactly the findings
        // this feature exists to record.
        var withFinding = (Slice(EventModelProvenance.Declared) with { EmittedEvents = [T<OrderPlaced>()] })
            .Merge(Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderShipped>()] });

        DisagreementsIn(withFinding).ShouldHaveSingleItem();

        var merged = withFinding.Merge(Slice(EventModelProvenance.Observed) with
        {
            Hotspots = [HotspotDescriptor.Prose("Does the CRM own the SLA clock?")],
        });

        DisagreementsIn(merged).ShouldHaveSingleItem();
        merged.Hotspots.Count.ShouldBe(2);
    }

    [Fact]
    public void the_same_disagreement_is_not_recorded_twice()
    {
        var derived = Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] };
        var observed = Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<AuditRecorded>()] };

        // The same observation arriving twice -- two CritterWatch nodes reporting the same slice --
        // is one finding, not two.
        var merged = derived.Merge(observed).Merge(observed);

        DisagreementsIn(merged).ShouldHaveSingleItem();
    }

    [Fact]
    public void disagreements_survive_assembly_of_a_whole_model()
    {
        var derived = new EventModelDescriptor("Orders", [
            Slice(EventModelProvenance.Derived) with { EmittedEvents = [T<OrderPlaced>()] }
        ]);

        var observed = new EventModelDescriptor("Orders", [
            Slice(EventModelProvenance.Observed) with { EmittedEvents = [T<OrderPlaced>(), T<AuditRecorded>()] }
        ]);

        var model = EventModelDescriptor.Merge("Orders", [derived, observed]);

        DisagreementsIn(model.Slices.ShouldHaveSingleItem()).ShouldHaveSingleItem();
    }

    #endregion

    public class PlaceOrder { }
    public class OrderHandler { }
    public class LegacyOrderHandler { }
    public class OrderPlaced { }
    public class OrderShipped { }
    public class AuditRecorded { }
}
