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

    #region which store, not just which rung (jasperfx#836)

    /// <summary>
    /// Two stores contributing the same slice leave a disagreement naming both — the merge's own half
    /// of jasperfx#836, which catches the case the store source's dedupe never sees because each
    /// store registered its own source.
    /// </summary>
    /// <remarks>
    /// Without an origin both claims render as "Derived claims …" against the same rung, which is
    /// exactly the unactionable shape the issue records: the survivor carried nothing to say which
    /// store it came from, and the loser left no trace at all.
    /// </remarks>
    [Fact]
    public void two_stores_contributing_one_slice_leave_a_disagreement_naming_both()
    {
        var ledger = Slice(EventModelProvenance.Derived) with { Origin = new Uri("store://ledger") };
        var audit = Slice(EventModelProvenance.Derived) with { Origin = new Uri("store://audit") };

        var merged = ledger.Merge(audit);

        // A tie, so first-wins -- and the loser is recorded rather than dropped.
        merged.Origin.ShouldBe(new Uri("store://ledger"));

        var hotspot = DisagreementsIn(merged).ShouldHaveSingleItem();
        hotspot.Role.ShouldBe(EventModelRole.Origin);
        hotspot.LosingClaim!.Value.ShouldBe("store://audit");
    }

    /// <summary>
    /// A declaration claims no origin, so it can never take one away — the same rule every other role
    /// follows, and what keeps a spec-declared slice from erasing the store behind the derived one.
    /// </summary>
    [Fact]
    public void a_declaration_neither_claims_nor_takes_away_an_origin()
    {
        var declared = Slice(EventModelProvenance.Declared) with { Domain = "Orders" };
        var derived = Slice(EventModelProvenance.Derived) with { Origin = new Uri("store://ledger") };

        foreach (var merged in new[] { declared.Merge(derived), derived.Merge(declared) })
        {
            merged.Origin.ShouldBe(new Uri("store://ledger"));
            merged.ProvenanceFor(EventModelRole.Origin).ShouldBe(EventModelProvenance.Derived);
            DisagreementsIn(merged).ShouldBeEmpty();
        }
    }

    /// <summary>
    /// Production outranks the code about the origin too, and the code's claim is still recorded.
    /// </summary>
    [Fact]
    public void a_higher_rung_takes_the_origin_and_the_lower_claim_is_recorded()
    {
        var derived = Slice(EventModelProvenance.Derived) with { Origin = new Uri("store://ledger") };
        var observed = Slice(EventModelProvenance.Observed) with { Origin = new Uri("store://audit") };

        var merged = derived.Merge(observed);

        merged.Origin.ShouldBe(new Uri("store://audit"));
        DisagreementsIn(merged).ShouldHaveSingleItem().WinningClaim
            .ShouldBe(new EventModelClaim(EventModelProvenance.Observed, "store://audit"));
    }

    #endregion

    #region which source, not just which rung (jasperfx#859)

    /// <summary>
    /// jasperfx#859's acceptance scenario: spec-first work has a declared model file <em>and</em>
    /// specs, both <see cref="EventModelProvenance.Declared"/> by construction. Rendered from the
    /// rung alone the finding read "Declared claims Automation; Declared claims Command" — one source
    /// apparently contradicting itself, with nothing to identify either party.
    /// </summary>
    [Fact]
    public void two_sources_on_one_rung_are_named_rather_than_rendered_as_their_shared_rung()
    {
        var model = Slice(EventModelProvenance.Declared) with
        {
            Origin = new Uri("file://CritterCrush.emodel.yaml"), Pattern = SlicePattern.Automation,
        };

        var specs = Slice(EventModelProvenance.Declared) with
        {
            Origin = new Uri("suite://CritterCrush.Specs"), Pattern = SlicePattern.Command,
        };

        var hotspot = DisagreementsIn(model.Merge(specs))
            .Single(x => x.Role == EventModelRole.Pattern);

        hotspot.Text.ShouldBe(
            "Pattern: file://CritterCrush.emodel.yaml claims Automation; suite://CritterCrush.Specs claims Command");

        // The structured form keeps the rung as well -- it is still how you decide which to trust.
        hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "Automation",
            "file://CritterCrush.emodel.yaml"));
        hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "Command",
            "suite://CritterCrush.Specs"));
    }

    /// <summary>
    /// A source that did not attribute itself falls back to its rung, so nothing that worked before
    /// jasperfx#859 renders differently.
    /// </summary>
    [Fact]
    public void an_unattributed_source_still_renders_as_its_rung()
    {
        var derived = Slice(EventModelProvenance.Derived) with { Domain = "Ordering" };
        var observed = Slice(EventModelProvenance.Observed) with { Domain = "Fulfillment" };

        var hotspot = DisagreementsIn(derived.Merge(observed)).ShouldHaveSingleItem();

        hotspot.Text.ShouldBe("Domain: Observed claims Fulfillment; Derived claims Ordering");
        hotspot.WinningClaim!.Source.ShouldBeNull();
        hotspot.WinningClaim.Claimant.ShouldBe("Observed");
    }

    /// <summary>
    /// Two unattributed sources sharing a rung still cannot be told apart from here — but the text no
    /// longer pretends one source said both things.
    /// </summary>
    [Fact]
    public void two_anonymous_sources_on_one_rung_say_so_instead_of_naming_the_rung_twice()
    {
        var first = Slice(EventModelProvenance.Declared) with { Domain = "Ordering" };
        var second = Slice(EventModelProvenance.Declared) with { Domain = "Fulfillment" };

        DisagreementsIn(first.Merge(second)).ShouldHaveSingleItem().Text
            .ShouldBe("Domain: two Declared sources disagree — kept Ordering, dropped Fulfillment");
    }

    /// <summary>
    /// One source contradicting itself is a real case — two slices it contributed under one origin —
    /// and it is the only one that should read that way.
    /// </summary>
    [Fact]
    public void one_source_contradicting_itself_says_which_source()
    {
        var first = Slice(EventModelProvenance.Declared) with
        {
            Origin = new Uri("file://CritterCrush.emodel.yaml"), Domain = "Ordering",
        };

        var second = Slice(EventModelProvenance.Declared) with
        {
            Origin = new Uri("file://CritterCrush.emodel.yaml"), Domain = "Fulfillment",
        };

        DisagreementsIn(first.Merge(second)).ShouldHaveSingleItem().Text
            .ShouldBe("Domain: file://CritterCrush.emodel.yaml contradicts itself — kept Ordering, dropped Fulfillment");
    }

    /// <summary>
    /// The <see cref="EventModelRole.Origin"/> role is the exception: its value already <em>is</em>
    /// the source, so naming the claimant too would render "store://ledger claims store://ledger".
    /// </summary>
    [Fact]
    public void the_origin_role_does_not_name_itself_twice()
    {
        var ledger = Slice(EventModelProvenance.Derived) with { Origin = new Uri("store://ledger") };
        var audit = Slice(EventModelProvenance.Derived) with { Origin = new Uri("store://audit") };

        var hotspot = DisagreementsIn(ledger.Merge(audit)).ShouldHaveSingleItem();

        hotspot.Role.ShouldBe(EventModelRole.Origin);
        hotspot.WinningClaim!.Source.ShouldBeNull();
        hotspot.Text.ShouldBe("Origin: two Derived sources disagree — kept store://ledger, dropped store://audit");
    }

    /// <summary>
    /// A source names itself once, on its slices; every role it then loses a claim on is attributed.
    /// </summary>
    [Fact]
    public void every_role_a_named_source_loses_names_that_source()
    {
        var declared = Slice(EventModelProvenance.Declared) with
        {
            Origin = new Uri("file://CritterCrush.emodel.yaml"),
            Domain = "Ordering",
            HandlerType = T<LegacyOrderHandler>(),
        };

        var derived = Slice(EventModelProvenance.Derived) with
        {
            Origin = new Uri("assembly://CritterCrush"),
            Domain = "Fulfillment",
            HandlerType = T<OrderHandler>(),
        };

        DisagreementsIn(declared.Merge(derived))
            .Where(x => x.Role != EventModelRole.Origin)
            .Select(x => x.Text)
            .ShouldBe([
                "HandlerType: assembly://CritterCrush claims OrderHandler; file://CritterCrush.emodel.yaml claims LegacyOrderHandler",
                "Domain: assembly://CritterCrush claims Fulfillment; file://CritterCrush.emodel.yaml claims Ordering",
            ]);
    }

    /// <summary>
    /// <see cref="HotspotDescriptor"/> compares its claims with <see cref="EventModelClaim"/>'s own
    /// record equality (jasperfx#853 writes the rest of it out by hand), so two findings that differ
    /// only in <em>which source</em> made the claim have to be two findings.
    /// </summary>
    /// <remarks>
    /// Which is the whole point: before jasperfx#859 they rendered identically and were one.
    /// </remarks>
    [Fact]
    public void two_findings_differing_only_by_source_are_not_the_same_finding()
    {
        var value = new EventModelClaim(EventModelProvenance.Declared, "Automation");
        var fromFile = value with { Source = "file://CritterCrush.emodel.yaml" };
        var fromSpecs = value with { Source = "suite://CritterCrush.Specs" };

        var loser = new EventModelClaim(EventModelProvenance.Declared, "Command");

        HotspotDescriptor.SourceDisagreement(EventModelRole.Pattern, fromFile, loser)
            .ShouldNotBe(HotspotDescriptor.SourceDisagreement(EventModelRole.Pattern, fromSpecs, loser));

        // ...and an unattributed claim is not equal to an attributed one either
        HotspotDescriptor.SourceDisagreement(EventModelRole.Pattern, value, loser)
            .ShouldNotBe(HotspotDescriptor.SourceDisagreement(EventModelRole.Pattern, fromFile, loser));
    }

    #endregion

    public class PlaceOrder { }
    public class OrderHandler { }
    public class LegacyOrderHandler { }
    public class OrderPlaced { }
    public class OrderShipped { }
    public class AuditRecorded { }
}

/// <summary>
/// jasperfx#798: a declared type and the real type it names are the same claim, not a disagreement.
/// </summary>
/// <remarks>
/// A declared type does not exist yet, so its FullName is synthesized from the model's single
/// namespace while the code puts types in whatever namespaces it likes — typically one per domain.
/// Keying on that guess made <c>CritterCrush.Appointment</c> and
/// <c>CritterCrush.Appointments.Appointment</c> disagree, and the hotspot then rendered both sides
/// as "Appointment": a thing disagreeing with itself. Six of six merged slices in the first real
/// model to reach this code path.
/// </remarks>
public class DeclaredTypeEqualityTests
{
    private const string Role = "AggregateTypes";

    /// <summary>A declaration: name-only, FullName synthesized, no assembly.</summary>
    private static TypeDescriptor Declared(string name, string ns) => new(name, $"{ns}.{name}", string.Empty);

    /// <summary>A real type: the namespace the code actually uses, and an assembly.</summary>
    private static TypeDescriptor Real(string name, string ns) => new(name, $"{ns}.{name}", "CritterCrush");

    private static EventModelSliceDescriptor Slice(EventModelProvenance provenance, params TypeDescriptor[] aggregates)
        => EventModelSliceDescriptor.Named("ConfirmAppointment") with
        {
            Provenance = provenance,
            AggregateTypes = aggregates,
        };

    private static IReadOnlyList<HotspotDescriptor> DisagreementsIn(EventModelSliceDescriptor slice)
        => slice.Hotspots.Where(x => x.Origin == HotspotOrigin.SourceDisagreement).ToList();

    [Fact]
    public void a_declared_type_matches_the_real_type_of_the_same_name()
    {
        var declared = Slice(EventModelProvenance.Declared, Declared("Appointment", "CritterCrush"));
        var derived = Slice(EventModelProvenance.Derived, Real("Appointment", "CritterCrush.Appointments"));

        var merged = declared.Merge(derived);

        DisagreementsIn(merged).ShouldBeEmpty();
        merged.AggregateTypes.Select(x => x.Name).ShouldBe(["Appointment"]);
    }

    [Fact]
    public void a_declared_type_the_code_does_not_have_still_disagrees()
    {
        // The relaxation must not swallow the case the merge exists for: the model says one thing
        // and the code does another.
        var declared = Slice(EventModelProvenance.Declared, Declared("Appointment", "CritterCrush"));
        var derived = Slice(EventModelProvenance.Derived, Real("Booking", "CritterCrush.Appointments"));

        var hotspot = DisagreementsIn(declared.Merge(derived)).ShouldHaveSingleItem();

        hotspot.Role.ShouldBe(Enum.Parse<EventModelRole>(Role));
        hotspot.WinningClaim!.Value.ShouldBe("Booking");
        hotspot.LosingClaim!.Value.ShouldBe("Appointment");
    }

    [Fact]
    public void two_real_types_of_the_same_name_disagree_and_say_which_is_which()
    {
        // Both sides real, so equality stays on FullName — and the message falls back to the full
        // name, because "claims Appointment; claims Appointment" is useless precisely here.
        var derived = Slice(EventModelProvenance.Derived, Real("Appointment", "CritterCrush.Appointments"));
        var observed = Slice(EventModelProvenance.Observed, Real("Appointment", "CritterCrush.Legacy"));

        var hotspot = DisagreementsIn(derived.Merge(observed)).ShouldHaveSingleItem();

        hotspot.WinningClaim!.Value.ShouldBe("CritterCrush.Legacy.Appointment");
        hotspot.LosingClaim!.Value.ShouldBe("CritterCrush.Appointments.Appointment");
    }
}
