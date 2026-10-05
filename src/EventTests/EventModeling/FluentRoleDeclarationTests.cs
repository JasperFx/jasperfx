using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling
{
    using Booking;

    #region sample_declaring_an_event_model_over_stub_types

    // Stubs first: the types exist, even if they carry nothing yet.
    public record ConfirmAppointment;
    public record AppointmentConfirmed;
    public record HomeCheckAssignmentAccepted;
    public record HomeCheckAppointmentProposed;
    public record SendDailyDigest;
    public record MyAppointments(Guid Id);
    public record Appointment;

    // ...then the model, declared against them. Strings stand in for anything not stubbed yet.
    public class BookingAppointments : EventModelDefinition
    {
        public override string Name => "CritterCrush";

        public override void Configure(EventModelBuilder model)
        {
            model.InDomain("Scheduling").InChapter("BookingAppointments");
            model.Aggregate<Appointment>();

            model.Command<ConfirmAppointment>()
                .TriggeredBy("Confirm Appointment", TriggerKind.Http)
                .Against<Appointment>()
                .Emits<AppointmentConfirmed>();

            model.Automation("ProposeHomeCheckAppointment")
                .On<HomeCheckAssignmentAccepted>()
                .StartsStream<Appointment>()
                .Emits<HomeCheckAppointmentProposed>();

            // A command fired by Wolverine's cron scheduling
            model.Automation<SendDailyDigest>();

            model.View<MyAppointments>()
                .From<HomeCheckAppointmentProposed>()
                .From<AppointmentConfirmed>();

            model.Command("RequestReschedule")
                .Against("Appointment")
                .Emits("AppointmentRescheduleRequested")
                .Hotspot("Can a member ask twice before the shelter answers?");
        }
    }

    #endregion

    /// <summary>
    /// jasperfx#957: the fluent model declares roles, by type or by name, on the Declared rung.
    /// </summary>
    public class FluentRoleDeclarationTests
    {
        private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

        private static async Task<EventModelDescriptor> declare(EventModelDefinition definition)
        {
            IEventModelDefinitionSource source = EventModelDefinitionSource.For(definition);
            var descriptor = await source.TryCreateAsync(new ServiceCollection().BuildServiceProvider(), default);
            return descriptor!.WithProvenance(source.Provenance);
        }

        private static async Task<EventModelSliceDescriptor> slice(string name)
            => (await declare(new BookingAppointments())).Slices.Single(x => x.Name == name);

        [Fact]
        public async Task a_command_slice_is_named_for_its_command_and_carries_its_roles()
        {
            var confirm = await slice("ConfirmAppointment");

            confirm.Pattern.ShouldBe(SlicePattern.Command);
            confirm.CommandType.ShouldBe(T<ConfirmAppointment>());
            confirm.TriggerLabel.ShouldBe("Confirm Appointment");
            confirm.TriggerKind.ShouldBe(TriggerKind.Http);
            confirm.AggregateTypes.ShouldBe([T<Appointment>()]);
            confirm.EmittedEvents.ShouldBe([T<AppointmentConfirmed>()]);
            confirm.Domain.ShouldBe("Scheduling");
            confirm.Chapter.ShouldBe("BookingAppointments");
            confirm.Provenance.ShouldBe(EventModelProvenance.Declared);
        }

        [Fact]
        public async Task an_event_triggered_automation_that_starts_a_stream()
        {
            var propose = await slice("ProposeHomeCheckAppointment");

            propose.Pattern.ShouldBe(SlicePattern.Automation);
            propose.ConsumedEvents.ShouldBe([T<HomeCheckAssignmentAccepted>()]);
            propose.StartsStream.ShouldBe(T<Appointment>());

            // A viewer that knows nothing of StartsStream still draws the aggregate
            propose.AggregateTypes.ShouldBe([T<Appointment>()]);
            propose.EmittedEvents.ShouldBe([T<HomeCheckAppointmentProposed>()]);
        }

        [Fact]
        public async Task a_scheduled_automation_is_named_for_its_command()
        {
            var digest = await slice("SendDailyDigest");

            digest.Pattern.ShouldBe(SlicePattern.Automation);
            digest.TriggerKind.ShouldBe(TriggerKind.JobScheduler);
            digest.CommandType.ShouldBe(T<SendDailyDigest>());
        }

        [Fact]
        public async Task a_view_is_named_for_its_read_model_and_folds_events()
        {
            var view = await slice("MyAppointments");

            view.Pattern.ShouldBe(SlicePattern.View);
            view.ReadModelTypes.ShouldBe([T<MyAppointments>()]);
            view.ConsumedEvents.ShouldBe([T<HomeCheckAppointmentProposed>(), T<AppointmentConfirmed>()]);
        }

        [Fact]
        public async Task names_stand_in_for_types_that_do_not_exist_yet()
        {
            var reschedule = await slice("RequestReschedule");

            reschedule.Pattern.ShouldBe(SlicePattern.Command);

            // A declaration: no namespace, no assembly -- which is what lets it match the real type later
            reschedule.CommandType.ShouldBe(new TypeDescriptor("RequestReschedule", "RequestReschedule", ""));
            reschedule.AggregateTypes.ShouldBe([new TypeDescriptor("Appointment", "Appointment", "")]);
            reschedule.EmittedEvents.Select(x => x.AssemblyName).ShouldBe([""]);
            reschedule.Hotspots.ShouldHaveSingleItem().Origin.ShouldBe(HotspotOrigin.Prose);
        }

        [Fact]
        public async Task the_model_declares_its_aggregates()
        {
            var model = await declare(new BookingAppointments());

            var aggregate = model.Aggregates.ShouldHaveSingleItem();
            aggregate.Type.ShouldBe(T<Appointment>());
            aggregate.Kind.ShouldBe(AggregateKind.WriteAggregate);
        }

        [Fact]
        public async Task the_code_wins_and_agreement_records_nothing()
        {
            var declared = await slice("ConfirmAppointment");

            // What Wolverine would derive once the endpoint exists: the same roles, by real type
            var derived = EventModelSliceDescriptor.Named("ConfirmAppointment") with
            {
                CommandType = T<ConfirmAppointment>(),
                HandlerType = T<ConfirmAppointmentEndpoint>(),
                AggregateTypes = [T<Appointment>()],
                EmittedEvents = [T<AppointmentConfirmed>()],
                TriggerKind = TriggerKind.Http,
                Origin = new Uri("event-model://wolverine-http/"),
                Provenance = EventModelProvenance.Derived,
            };

            var merged = declared.Merge(derived);

            merged.HandlerType.ShouldBe(T<ConfirmAppointmentEndpoint>());
            merged.Pattern.ShouldBe(SlicePattern.Command);
            merged.TriggerLabel.ShouldBe("Confirm Appointment");
            merged.Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public async Task a_name_declared_before_the_type_existed_agrees_with_the_real_type()
        {
            var model = new EventModelBuilder();
            model.Command("ConfirmAppointment").Against("Appointment").Emits("AppointmentConfirmed");
            var declared = model.Build("app").Slices.Single() with { Provenance = EventModelProvenance.Declared };

            var derived = EventModelSliceDescriptor.Named("ConfirmAppointment") with
            {
                CommandType = T<ConfirmAppointment>(),
                AggregateTypes = [T<Appointment>()],
                EmittedEvents = [T<AppointmentConfirmed>()],
                Provenance = EventModelProvenance.Derived,
            };

            declared.Merge(derived).Hotspots.ShouldBeEmpty();
        }

        [Fact]
        public async Task where_the_code_differs_the_difference_is_the_to_do_list()
        {
            var declared = await slice("ConfirmAppointment");

            var derived = EventModelSliceDescriptor.Named("ConfirmAppointment") with
            {
                EmittedEvents = [T<HomeCheckAppointmentProposed>()],
                Origin = new Uri("event-model://wolverine-http/"),
                Provenance = EventModelProvenance.Derived,
            };

            var hotspot = declared.Merge(derived).Hotspots.ShouldHaveSingleItem();
            hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
            hotspot.Role.ShouldBe(EventModelRole.EmittedEvents);
            hotspot.LosingClaim!.Source.ShouldBe("event-model://CritterCrush");
        }

        [Fact]
        public void starts_stream_is_a_role_that_claims_and_disagrees_by_identity()
        {
            var declared = EventModelSliceDescriptor.Named("Propose") with
            {
                StartsStream = T<Appointment>(),
                Provenance = EventModelProvenance.Declared,
            };
            var derived = EventModelSliceDescriptor.Named("Propose") with
            {
                StartsStream = T<Legacy.Appointment>(),
                Provenance = EventModelProvenance.Derived,
            };

            declared.Claims(EventModelRole.StartsStream).ShouldBeTrue();

            var merged = declared.Merge(derived);
            merged.StartsStream.ShouldBe(T<Legacy.Appointment>());
            merged.ProvenanceFor(EventModelRole.StartsStream).ShouldBe(EventModelProvenance.Derived);

            var hotspot = merged.Hotspots.ShouldHaveSingleItem();
            hotspot.Role.ShouldBe(EventModelRole.StartsStream);
            hotspot.LosingClaim!.Value.ShouldBe(typeof(Appointment).FullName);
        }

        [Fact]
        public void the_new_role_is_appended_so_the_wire_integers_do_not_move()
        {
            ((int)EventModelRole.StartsStream).ShouldBe((int)EventModelRole.Origin + 1);
            Enum.GetValues<EventModelRole>().Max().ShouldBe(EventModelRole.StartsStream);
        }

        [Fact]
        public void a_generic_command_is_named_the_way_wolverine_names_it()
        {
            var model = new EventModelBuilder();
            model.Command<Envelope<ConfirmAppointment>>();

            model.Build("app").Slices.Single().Name.ShouldBe("Envelope<ConfirmAppointment>");
        }

        [Fact]
        public void a_role_declared_twice_is_one_role()
        {
            var model = new EventModelBuilder();
            model.Command<ConfirmAppointment>().Emits<AppointmentConfirmed>().Emits<AppointmentConfirmed>()
                .Emits("AppointmentConfirmed");

            model.Build("app").Slices.Single().EmittedEvents.ShouldBe([T<AppointmentConfirmed>()]);
        }

        [Fact]
        public void a_declared_name_cannot_be_blank()
        {
            var model = new EventModelBuilder();
            Should.Throw<ArgumentException>(() => model.Command("Confirm").Emits(" "));
        }

        [Fact]
        public void the_escape_hatch_still_folds_in()
        {
            var model = new EventModelBuilder();
            model.Slice("PartnerShipment")
                .InDomain("Partners")
                .ForFlowNotOwnedHere(r => r.Command<ConfirmAppointment>().Emits<AppointmentConfirmed>());

            var built = model.Build("app").Slices.Single();
            built.Domain.ShouldBe("Partners");
            built.CommandType.ShouldBe(T<ConfirmAppointment>());
            built.EmittedEvents.ShouldBe([T<AppointmentConfirmed>()]);
        }
    }
}

namespace EventTests.EventModeling.Booking
{
    public class ConfirmAppointmentEndpoint;

    public record Envelope<T>(T Body);
}

namespace EventTests.EventModeling.Legacy
{
    public record Appointment;
}
