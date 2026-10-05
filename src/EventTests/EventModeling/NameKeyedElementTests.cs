using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling
{

/// <summary>
/// jasperfx#958: a type declared by name and the real type it becomes are ONE element, one link end and
/// one aggregate — not two stickies.
/// </summary>
public class NameKeyedElementTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    // A declaration: the type does not exist yet, so there is no namespace and no assembly (#798).
    private static TypeDescriptor Declared(string name) => new(name, name, "");

    [Fact]
    public void a_consumed_event_declared_by_name_folds_into_the_real_emitted_event()
    {
        var slice = EventModelSliceDescriptor.Named("ConfirmAppointment") with
        {
            EmittedEvents = [T<NameKeyed.AppointmentConfirmed>()],
            ConsumedEvents = [Declared("AppointmentConfirmed")],
        };

        slice.Elements.Where(x => x.Kind == EventModelElementKind.Event)
            .ShouldHaveSingleItem().Type!.FullName.ShouldBe(typeof(NameKeyed.AppointmentConfirmed).FullName);
    }

    [Fact]
    public void a_read_model_read_by_name_folds_into_the_real_produced_read_model()
    {
        var slice = EventModelSliceDescriptor.Named("MyAppointments") with
        {
            ReadModelTypes = [T<NameKeyed.MyAppointments>()],
            ReadsFrom = [Declared("MyAppointments")],
        };

        slice.Elements.Where(x => x.Kind == EventModelElementKind.ReadModel)
            .ShouldHaveSingleItem().Type!.FullName.ShouldBe(typeof(NameKeyed.MyAppointments).FullName);
    }

    [Fact]
    public void different_real_types_with_one_simple_name_stay_two_elements()
    {
        var slice = EventModelSliceDescriptor.Named("Reconcile") with
        {
            EmittedEvents = [T<NameKeyed.AppointmentConfirmed>()],
            ConsumedEvents = [T<NameKeyed.Legacy.AppointmentConfirmed>()],
        };

        slice.Elements.Count(x => x.Kind == EventModelElementKind.Event).ShouldBe(2);
    }

    [Fact]
    public void a_consumed_link_ends_on_the_sticky_the_consuming_slice_draws()
    {
        var confirm = EventModelSliceDescriptor.Named("ConfirmAppointment") with
        {
            EmittedEvents = [T<NameKeyed.AppointmentConfirmed>()],
        };

        // Re-emits the event (real) and also declares it consumed (by name): it draws ONE sticky.
        var reconfirm = EventModelSliceDescriptor.Named("Reconfirm") with
        {
            EmittedEvents = [T<NameKeyed.AppointmentConfirmed>()],
            ConsumedEvents = [Declared("AppointmentConfirmed")],
        };

        var model = new EventModelDescriptor("app", [confirm, reconfirm]);

        var link = model.Links.Single(x => x.Kind == EventModelLinkKind.EventConsumed && x.ToSlice == "Reconfirm");
        reconfirm.Elements.Select(x => x.Id).ShouldContain(link.ToElementId);
    }

    [Fact]
    public void a_declared_aggregate_and_the_real_one_are_one_aggregate_whichever_comes_first()
    {
        var declared = new EventModelDescriptor("app", []) with
        {
            Aggregates = [new AggregateDescriptor(Declared("Appointment"), AggregateKind.WriteAggregate, [])],
        };
        var derived = new EventModelDescriptor("app", []) with
        {
            Aggregates = [new AggregateDescriptor(T<NameKeyed.Appointment>(), AggregateKind.WriteAggregate,
                [T<NameKeyed.AppointmentConfirmed>()])],
        };

        foreach (var order in new[] { new[] { declared, derived }, new[] { derived, declared } })
        {
            var aggregate = EventModelDescriptor.Merge("app", order).Aggregates.ShouldHaveSingleItem();

            // The real type wins either way: it carries the namespace and the applied events.
            aggregate.Type.FullName.ShouldBe(typeof(NameKeyed.Appointment).FullName);
            aggregate.AppliedEvents.ShouldHaveSingleItem();
        }
    }

    [Fact]
    public void two_real_aggregates_with_one_simple_name_stay_two()
    {
        var model = new EventModelDescriptor("app", []) with
        {
            Aggregates =
            [
                new AggregateDescriptor(T<NameKeyed.Appointment>(), AggregateKind.WriteAggregate, []),
                new AggregateDescriptor(T<NameKeyed.Legacy.Appointment>(), AggregateKind.WriteAggregate, []),
            ],
        };

        EventModelDescriptor.Merge("app", [model]).Aggregates.Count.ShouldBe(2);
    }
}
}

namespace EventTests.EventModeling.NameKeyed
{
    public record AppointmentConfirmed(Guid AppointmentId);

    public record MyAppointments(Guid Id);

    public record Appointment(Guid Id);
}

namespace EventTests.EventModeling.NameKeyed.Legacy
{
    public record AppointmentConfirmed(Guid AppointmentId);

    public record Appointment(Guid Id);
}
