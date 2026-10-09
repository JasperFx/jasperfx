using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#994 — <c>ForAggregate</c> sets the aggregate the following command slices decide against;
/// <c>Against</c>, <c>StartsStream</c>, <c>NoAggregate</c> and <c>DeciderModel</c> override it.
/// </summary>
public class ForAggregateTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static EventModelSliceDescriptor slice(EventModelBuilder model, string name)
        => model.BuildSlices().Single(x => x.Name == name);

    [Fact]
    public void the_default_applies_to_commands_that_follow_and_not_to_earlier_ones()
    {
        var model = new EventModelBuilder();
        model.Command<ProposeAppointment>();
        model.ForAggregate<Appointment>();
        model.Command<ConfirmAppointment>().Emits<AppointmentConfirmed>();

        var before = slice(model, nameof(ProposeAppointment));
        before.AggregateTypes.ShouldBeEmpty();
        before.AggregateDeclaration.ShouldBeNull();

        var after = slice(model, nameof(ConfirmAppointment));
        after.AggregateTypes.ShouldHaveSingleItem().ShouldBe(T<Appointment>());
        after.AggregateDeclaration.ShouldBe(AggregateDeclaration.Default);
    }

    [Fact]
    public void the_default_can_be_named_before_the_aggregate_type_exists()
    {
        var model = new EventModelBuilder();
        model.ForAggregate("Appointment");
        model.Command("ConfirmAppointment");

        var confirm = slice(model, "ConfirmAppointment");
        confirm.AggregateTypes.ShouldHaveSingleItem().Name.ShouldBe("Appointment");
        confirm.AggregateDeclaration.ShouldBe(AggregateDeclaration.Default);
    }

    [Fact]
    public void for_aggregate_also_declares_the_aggregate_on_the_model()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();

        model.Build("M").Aggregates.ShouldHaveSingleItem().Type.ShouldBe(T<Appointment>());
    }

    [Fact]
    public void an_explicit_against_replaces_the_default()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();
        model.Command<RescheduleAppointment>().Against<Calendar>();

        var reschedule = slice(model, nameof(RescheduleAppointment));
        reschedule.AggregateTypes.ShouldHaveSingleItem().ShouldBe(T<Calendar>());
        reschedule.AggregateDeclaration.ShouldBe(AggregateDeclaration.Explicit);
    }

    [Fact]
    public void a_slice_that_starts_a_stream_ignores_the_default()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Calendar>();
        model.Command<ProposeAppointment>().StartsStream<Appointment>();

        var propose = slice(model, nameof(ProposeAppointment));
        propose.AggregateTypes.ShouldHaveSingleItem().ShouldBe(T<Appointment>());
        propose.StartsStream.ShouldBe(T<Appointment>());
        propose.AggregateDeclaration.ShouldBe(AggregateDeclaration.Explicit);
    }

    [Fact]
    public void no_aggregate_overrides_the_default()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();
        model.Command<RecordWalkIn>().NoAggregate().Emits<WalkInRecorded>();

        var walkIn = slice(model, nameof(RecordWalkIn));
        walkIn.AggregateTypes.ShouldBeEmpty();
        walkIn.AggregateDeclaration.ShouldBe(AggregateDeclaration.None);
    }

    [Fact]
    public void a_decider_model_overrides_the_default()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();
        model.Command<BookSlot>().DeciderModel<SlotBooking>().Emits<SlotBooked>();

        var book = slice(model, nameof(BookSlot));
        book.AggregateTypes.ShouldBeEmpty();
        book.DeciderModel.ShouldBe(T<SlotBooking>());
        book.AggregateDeclaration.ShouldBe(AggregateDeclaration.DeciderModel);
    }

    [Fact]
    public void a_decider_model_can_be_named_before_its_type_exists()
    {
        var model = new EventModelBuilder();
        model.Command("BookSlot").DeciderModel("SlotBooking");

        slice(model, "BookSlot").DeciderModel!.Name.ShouldBe("SlotBooking");
    }

    [Fact]
    public void no_aggregate_and_an_explicit_aggregate_contradict_each_other()
    {
        var model = new EventModelBuilder();

        Should.Throw<InvalidOperationException>(() => model.Command("A").Against<Appointment>().NoAggregate());
        Should.Throw<InvalidOperationException>(() => model.Command("B").NoAggregate().Against<Appointment>());
    }

    [Fact]
    public void a_second_for_aggregate_replaces_the_first()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();
        model.Command<ConfirmAppointment>();
        model.ForAggregate<Calendar>();
        model.Command<RescheduleAppointment>();

        slice(model, nameof(ConfirmAppointment)).AggregateTypes.ShouldHaveSingleItem().ShouldBe(T<Appointment>());
        slice(model, nameof(RescheduleAppointment)).AggregateTypes.ShouldHaveSingleItem().ShouldBe(T<Calendar>());
    }

    [Fact]
    public void views_automations_and_translations_are_untouched()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();
        model.View<AppointmentSchedule>().From<AppointmentConfirmed>();
        model.Automation("RemindPatient").On<AppointmentConfirmed>();
        model.Translation("ImportFromPartner");
        model.Slice("Unpatterned");

        foreach (var name in new[] { nameof(AppointmentSchedule), "RemindPatient", "ImportFromPartner", "Unpatterned" })
        {
            var untouched = slice(model, name);
            untouched.AggregateTypes.ShouldBeEmpty();
            untouched.AggregateDeclaration.ShouldBeNull();
        }
    }

    [Fact]
    public void a_slice_opened_generically_and_marked_a_command_takes_the_default()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();
        model.Slice("Confirm").Pattern(SlicePattern.Command);

        slice(model, "Confirm").AggregateDeclaration.ShouldBe(AggregateDeclaration.Default);
    }

    [Fact]
    public async Task the_default_does_not_carry_into_the_next_definition()
    {
        using var services = new ServiceCollection()
            .AddEventModel<AppointmentsChapter>()
            .AddEventModel<WalkInsChapter>()
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.AssembleAsync(services)).ShouldHaveSingleItem();

        model.Slices.Single(x => x.Name == nameof(ConfirmAppointment)).AggregateTypes
            .ShouldHaveSingleItem().ShouldBe(T<Appointment>());

        var walkIn = model.Slices.Single(x => x.Name == nameof(RecordWalkIn));
        walkIn.AggregateTypes.ShouldBeEmpty();
        walkIn.AggregateDeclaration.ShouldBeNull();
        walkIn.Chapter.ShouldBeNull();
    }

    [Fact]
    public void a_default_applied_aggregate_is_a_declared_claim_that_the_code_overrides()
    {
        var model = new EventModelBuilder();
        model.ForAggregate<Appointment>();
        model.Command<ConfirmAppointment>();
        var declared = slice(model, nameof(ConfirmAppointment)).WithProvenance(EventModelProvenance.Declared);

        var derived = EventModelSliceDescriptor.Named(nameof(ConfirmAppointment)) with
        {
            AggregateTypes = [T<Calendar>()],
            Provenance = EventModelProvenance.Derived,
        };

        var merged = declared.Merge(derived);

        merged.AggregateTypes.ShouldHaveSingleItem().ShouldBe(T<Calendar>());
        merged.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.SourceDisagreement);

        // Only the declaration says why, so its reason survives the merge with the code.
        merged.AggregateDeclaration.ShouldBe(AggregateDeclaration.Default);
    }

    public class AppointmentsChapter : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            model.InChapter("Appointments");
            model.ForAggregate<Appointment>();
            model.Command<ConfirmAppointment>();
        }
    }

    public class WalkInsChapter : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model) => model.Command<RecordWalkIn>();
    }

    public record Appointment;
    public record Calendar;
    public record SlotBooking;
    public record AppointmentSchedule;
    public record ProposeAppointment;
    public record ConfirmAppointment;
    public record RescheduleAppointment;
    public record RecordWalkIn;
    public record BookSlot;
    public record AppointmentConfirmed;
    public record WalkInRecorded;
    public record SlotBooked;
}
