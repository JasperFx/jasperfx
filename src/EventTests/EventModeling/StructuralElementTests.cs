using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling.Structure;

/// <summary>
/// jasperfx#1298: the five structural elements — an actor on the trigger, external events, schedules on
/// automations, screen names and descriptions.
/// </summary>
public class StructuralElementTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    #region sample_tests_structural_elements

    public record ApplyToVolunteer;
    public record VolunteerApplied;
    public record PaymentCaptured;
    public record PaymentRecorded;
    public record SendWeeklyDigest;
    public record Volunteer;
    public class VolunteerRoster;

    public class VolunteeringModel : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            model.Command<ApplyToVolunteer>()
                // Who, and what they act through
                .TriggeredBy("Volunteer submits the application", TriggerKind.Human)
                .Actor("Volunteer", "volunteer")
                .FromScreen("Application form")
                .Describe("A member of the public offers to help at an event.")
                .Describe<VolunteerApplied>("Raised once the application is complete; nothing has been vetted yet.")
                .StartsStream<Volunteer>()
                .Emits<VolunteerApplied>();

            // An event from outside the model, translated in
            model.Translation("RecordPayment")
                .From<PaymentCaptured>().External("Payments")
                .Emits<PaymentRecorded>();

            // A scheduled automation, with its cadence
            model.Automation<SendWeeklyDigest>()
                .OnCron("0 9 * * MON")
                .Reads<VolunteerRoster>();
        }
    }

    #endregion

    private static EventModelDescriptor build()
    {
        var model = new EventModelBuilder();
        new VolunteeringModel().Configure(model);
        return model.Build("Volunteering");
    }

    private static EventModelSliceDescriptor slice(string name) => build().Slices.Single(x => x.Name == name);

    // ---- actor ----

    [Fact]
    public void an_actor_is_declared_by_name_with_required_roles()
    {
        var apply = slice(nameof(ApplyToVolunteer));

        apply.Actor.ShouldBe(new ActorDescriptor("Volunteer", ["volunteer"]));
        apply.TriggerLabel.ShouldBe("Volunteer submits the application");
        apply.Claims(EventModelRole.Actor).ShouldBeTrue();
    }

    [Fact]
    public void a_role_verb_after_a_human_trigger_makes_the_label_the_actor()
    {
        var model = new EventModelBuilder();
        model.Command<ApplyToVolunteer>()
            .TriggeredBy("Volunteer", TriggerKind.Human)
            .RequiringRole("volunteer")
            .RequiringRoles("verified", "volunteer");

        model.BuildSlices().Single().Actor.ShouldBe(new ActorDescriptor("Volunteer", ["volunteer", "verified"]));
    }

    [Fact]
    public void a_role_with_nobody_to_require_it_of_throws_at_build()
    {
        var model = new EventModelBuilder();
        model.Command<ApplyToVolunteer>().RequiringRole("volunteer");

        Should.Throw<InvalidOperationException>(() => model.BuildSlices()).Message.ShouldContain("Actor(");
    }

    [Fact]
    public void a_slice_with_no_actor_verbs_has_no_actor()
    {
        var model = new EventModelBuilder();
        model.Command<ApplyToVolunteer>().TriggeredBy("Volunteer clicks Apply", TriggerKind.Human);

        model.BuildSlices().Single().Actor.ShouldBeNull();
    }

    [Fact]
    public void declared_and_derived_required_roles_disagreeing_is_a_real_finding()
    {
        var declared = EventModelSliceDescriptor.Named("ApplyToVolunteer") with
        {
            Actor = new ActorDescriptor("Volunteer", ["volunteer"]),
            Provenance = EventModelProvenance.Declared,
        };
        var derived = EventModelSliceDescriptor.Named("ApplyToVolunteer") with
        {
            Actor = new ActorDescriptor("Volunteer", ["volunteer", "verified"]),
            Provenance = EventModelProvenance.Derived,
        };

        var merged = declared.Merge(derived);

        merged.Actor!.RequiredRoles.ShouldBe(["volunteer", "verified"]);
        var hotspot = merged.Hotspots.ShouldHaveSingleItem();
        hotspot.Role.ShouldBe(EventModelRole.Actor);
        hotspot.Text.ShouldBe("Actor: Derived claims Volunteer [volunteer, verified]; Declared claims Volunteer [volunteer]");
    }

    [Fact]
    public void the_same_actor_from_two_rungs_is_silent()
    {
        var declared = EventModelSliceDescriptor.Named("x") with { Actor = new ActorDescriptor("Volunteer"), Provenance = EventModelProvenance.Declared };
        var derived = EventModelSliceDescriptor.Named("x") with { Actor = new ActorDescriptor("Volunteer", []), Provenance = EventModelProvenance.Derived };

        declared.Merge(derived).Hotspots.ShouldBeEmpty();
    }

    // ---- external events ----

    [Fact]
    public void an_external_event_ties_the_consumed_event_to_an_inbound_system()
    {
        var record = slice("RecordPayment");

        record.ConsumedEvents.ShouldBe([T<PaymentCaptured>()]);
        record.ExternalEvents.ShouldBe([new ExternalEventDescriptor(T<PaymentCaptured>(), "Payments")]);
        record.ExternalSystemFor(T<PaymentCaptured>()).ShouldBe("Payments");
        record.ExternalSystemFor(T<PaymentRecorded>()).ShouldBeNull();
        record.ExternalSystems.ShouldBe([new ExternalSystemDescriptor("Payments", ExternalSystemDirection.Inbound)]);
    }

    [Fact]
    public void external_does_not_add_a_second_system_already_declared()
    {
        var model = new EventModelBuilder();
        model.Translation("RecordPayment")
            .ExternalSystem("Payments", ExternalSystemDirection.Inbound, "rabbitmq://queue/payments")
            .From<PaymentCaptured>().External("Payments");

        model.BuildSlices().Single().ExternalSystems.ShouldHaveSingleItem().EndpointUri.ShouldBe("rabbitmq://queue/payments");
    }

    [Fact]
    public void external_needs_a_consumed_event_first_and_one_system_per_event()
    {
        Should.Throw<InvalidOperationException>(() => new EventModelBuilder().Translation("x").External("Payments"))
            .Message.ShouldContain("On() or From()");

        var builder = new EventModelBuilder().Translation("x").From<PaymentCaptured>().External("Payments");
        builder.External("Payments"); // saying it twice is harmless
        Should.Throw<InvalidOperationException>(() => builder.External("Stripe"));
    }

    [Fact]
    public void an_external_flag_survives_the_merge_with_code_and_disagrees_across_rungs()
    {
        var declared = EventModelSliceDescriptor.Named("RecordPayment") with
        {
            ConsumedEvents = [T<PaymentCaptured>()],
            ExternalEvents = [new ExternalEventDescriptor(T<PaymentCaptured>(), "Payments")],
            Provenance = EventModelProvenance.Declared,
        };
        var derived = EventModelSliceDescriptor.Named("RecordPayment") with
        {
            ConsumedEvents = [T<PaymentCaptured>()],
            Provenance = EventModelProvenance.Derived,
        };

        derived.Merge(declared).ExternalSystemFor(T<PaymentCaptured>()).ShouldBe("Payments");

        var otherSystem = derived with { ExternalEvents = [new ExternalEventDescriptor(T<PaymentCaptured>(), "Stripe")] };
        var merged = declared.Merge(otherSystem);
        merged.ExternalSystemFor(T<PaymentCaptured>()).ShouldBe("Stripe");
        merged.Hotspots.ShouldHaveSingleItem().Text
            .ShouldBe("ExternalEvents: Derived claims PaymentCaptured from Stripe; Declared claims PaymentCaptured from Payments");
    }

    // ---- schedule ----

    [Fact]
    public void an_automation_carries_its_schedule_beside_the_job_scheduler_trigger()
    {
        var digest = slice(nameof(SendWeeklyDigest));

        digest.TriggerKind.ShouldBe(TriggerKind.JobScheduler);
        digest.Schedule.ShouldBe(ScheduleDescriptor.OnCron("0 9 * * MON"));
        digest.Schedule!.ToString().ShouldBe("cron 0 9 * * MON");
    }

    [Fact]
    public void every_and_scheduled_are_the_other_two_forms_and_stamp_the_trigger_kind()
    {
        var model = new EventModelBuilder();
        model.Automation("Nightly").Every(TimeSpan.FromHours(24));
        model.Automation("Payroll").Scheduled("month-end");
        model.Automation("Already").TriggeredBy(TriggerKind.External).Every(TimeSpan.FromMinutes(5));

        var slices = model.BuildSlices();
        slices[0].Schedule.ShouldBe(ScheduleDescriptor.EveryInterval(TimeSpan.FromHours(24)));
        slices[0].TriggerKind.ShouldBe(TriggerKind.JobScheduler);
        slices[1].Schedule.ShouldBe(ScheduleDescriptor.Named("month-end"));
        // A trigger kind already claimed is left alone
        slices[2].TriggerKind.ShouldBe(TriggerKind.External);
    }

    [Fact]
    public void a_schedule_refuses_nonsense()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ScheduleDescriptor.EveryInterval(TimeSpan.Zero));
        Should.Throw<ArgumentException>(() => ScheduleDescriptor.OnCron(" "));
        Should.Throw<ArgumentException>(() => ScheduleDescriptor.Named(""));
    }

    [Fact]
    public void a_schedule_merges_as_a_scalar()
    {
        var declared = EventModelSliceDescriptor.Named("x") with { Schedule = ScheduleDescriptor.OnCron("0 9 * * MON"), Provenance = EventModelProvenance.Declared };
        var derived = EventModelSliceDescriptor.Named("x") with { Schedule = ScheduleDescriptor.EveryInterval(TimeSpan.FromDays(7)), Provenance = EventModelProvenance.Derived };

        var merged = declared.Merge(derived);
        merged.Schedule.ShouldBe(derived.Schedule);
        merged.Hotspots.ShouldHaveSingleItem().Text.ShouldBe("Schedule: Derived claims every 7.00:00:00; Declared claims cron 0 9 * * MON");
    }

    // ---- screen ----

    [Fact]
    public void a_screen_is_a_name_and_a_wireframe_element_pointing_at_the_trigger()
    {
        var apply = slice(nameof(ApplyToVolunteer));

        apply.Screen.ShouldBe("Application form");

        var screen = apply.Elements.Single(x => x.Kind == EventModelElementKind.Screen);
        screen.Lane.ShouldBe(EventModelLane.Wireframe);
        screen.Label.ShouldBe("Application form");
        screen.Provenance.ShouldBe(EventModelProvenance.Declared);
        EventModelPalette.ColorFor(EventModelElementKind.Screen).ShouldBe("#FFFFFF");

        var trigger = apply.Elements.Single(x => x.Kind == EventModelElementKind.Trigger);
        apply.Edges.ShouldContain(new EventModelEdge(screen.Id, trigger.Id));
    }

    [Fact]
    public void a_screen_with_no_trigger_points_at_the_command()
    {
        var model = new EventModelBuilder();
        model.Command<ApplyToVolunteer>().FromScreen("Application form");

        var built = model.BuildSlices().Single();
        var screen = built.Elements.Single(x => x.Kind == EventModelElementKind.Screen);
        var command = built.Elements.Single(x => x.Kind == EventModelElementKind.Command);
        built.Edges.ShouldContain(new EventModelEdge(screen.Id, command.Id));
    }

    // ---- descriptions ----

    [Fact]
    public void descriptions_land_on_the_slice_and_on_its_elements()
    {
        var apply = slice(nameof(ApplyToVolunteer));

        apply.Description.ShouldBe("A member of the public offers to help at an event.");
        apply.DescriptionOf(T<VolunteerApplied>()).ShouldBe("Raised once the application is complete; nothing has been vetted yet.");
        apply.DescriptionOf(T<ApplyToVolunteer>()).ShouldBeNull();
        apply.Claims(EventModelRole.Description).ShouldBeTrue();

        // The element carries it, so a viewer shows it on hover without a join
        apply.Elements.Single(x => x.Kind == EventModelElementKind.Event).Description
            .ShouldBe("Raised once the application is complete; nothing has been vetted yet.");
        apply.Elements.Single(x => x.Kind == EventModelElementKind.Command).Description.ShouldBeNull();
    }

    [Fact]
    public void an_element_description_by_name_matches_the_real_type()
    {
        var model = new EventModelBuilder();
        model.Command<ApplyToVolunteer>().Emits<VolunteerApplied>().Describe("VolunteerApplied", "By name, before the type existed");

        model.BuildSlices().Single().DescriptionOf(T<VolunteerApplied>()).ShouldBe("By name, before the type existed");
    }

    [Fact]
    public void the_richer_description_wins_and_nothing_is_ever_recorded()
    {
        var declared = EventModelSliceDescriptor.Named("x") with
        {
            Description = "A member of the public offers to help at an event, which an organiser then vets.",
            ElementDescriptions = [new ElementDescription(T<VolunteerApplied>(), "Short.")],
            Provenance = EventModelProvenance.Declared,
        };
        var derived = EventModelSliceDescriptor.Named("x") with
        {
            Description = "Applies to volunteer.",
            ElementDescriptions = [new ElementDescription(T<VolunteerApplied>(), "Raised once the application is complete."),
                new ElementDescription(T<ApplyToVolunteer>(), "The command.")],
            Provenance = EventModelProvenance.Derived,
        };

        foreach (var merged in new[] { declared.Merge(derived), derived.Merge(declared) })
        {
            merged.Description.ShouldBe(declared.Description);
            merged.DescriptionOf(T<VolunteerApplied>()).ShouldBe("Raised once the application is complete.");
            merged.DescriptionOf(T<ApplyToVolunteer>()).ShouldBe("The command.");
            merged.Hotspots.ShouldBeEmpty();
            merged.ProvenanceFor(EventModelRole.Description).ShouldBe(EventModelProvenance.Declared);
        }
    }

    [Fact]
    public void a_description_only_one_side_has_is_kept()
    {
        var declared = EventModelSliceDescriptor.Named("x") with { Description = "Only here.", Provenance = EventModelProvenance.Declared };
        var derived = EventModelSliceDescriptor.Named("x") with { Provenance = EventModelProvenance.Derived };

        var merged = derived.Merge(declared);
        merged.Description.ShouldBe("Only here.");
        merged.ProvenanceFor(EventModelRole.Description).ShouldBe(EventModelProvenance.Declared);
    }

    // ---- wire ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void everything_round_trips(bool critterWatchWire)
    {
        var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();
        var descriptor = build();

        var back = JsonSerializer.Deserialize<EventModelDescriptor>(JsonSerializer.Serialize(descriptor, options), options)!;

        var apply = back.Slices.Single(x => x.Name == nameof(ApplyToVolunteer));
        apply.Actor.ShouldBe(new ActorDescriptor("Volunteer", ["volunteer"]));
        apply.Screen.ShouldBe("Application form");
        apply.Description.ShouldBe("A member of the public offers to help at an event.");
        apply.ElementDescriptions.ShouldBe(descriptor.Slices[0].ElementDescriptions);
        apply.Elements.Count(x => x.Kind == EventModelElementKind.Screen).ShouldBe(1);

        back.Slices.Single(x => x.Name == "RecordPayment").ExternalEvents
            .ShouldBe([new ExternalEventDescriptor(T<PaymentCaptured>(), "Payments")]);

        back.Slices.Single(x => x.Name == nameof(SendWeeklyDigest)).Schedule.ShouldBe(ScheduleDescriptor.OnCron("0 9 * * MON"));

        // An interval survives too
        var every = EventModelSliceDescriptor.Named("x") with { Schedule = ScheduleDescriptor.EveryInterval(TimeSpan.FromMinutes(5)) };
        JsonSerializer.Deserialize<EventModelSliceDescriptor>(JsonSerializer.Serialize(every, options), options)!
            .Schedule.ShouldBe(every.Schedule);
    }
}
