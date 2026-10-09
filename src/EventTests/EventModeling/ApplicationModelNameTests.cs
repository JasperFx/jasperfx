using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#992 — <see cref="EventModelDefinition.Name"/> defaults to the application's model rather than
/// the class name, so one definition per chapter merges with the slices the code derives.
/// </summary>
public class ApplicationModelNameTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static ServiceProvider Services(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new JasperFxOptions { ServiceName = "CritterCrush" });
        configure(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void the_application_model_is_the_service_name()
    {
        using var services = Services(_ => { });
        EventModelDiscovery.ApplicationModelName(services).ShouldBe("CritterCrush");
    }

    [Fact]
    public void without_jasperfx_options_the_application_model_falls_back_to_the_entry_assembly()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var expected = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
                       ?? ProjectionEventModelSource.DefaultModelName;

        EventModelDiscovery.ApplicationModelName(services).ShouldBe(expected);
    }

    [Fact]
    public async Task two_per_chapter_definitions_without_a_name_assemble_into_the_code_derived_model()
    {
        using var services = Services(x => x
            .AddEventModelSource(new CodeDerivedSource())
            .AddEventModel<BookingAppointmentsModel>()
            .AddEventModel<BillingModel>());

        var model = (await EventModelDiscovery.AssembleAsync(services)).ShouldHaveSingleItem();

        model.Name.ShouldBe("CritterCrush");
        model.Slices.Select(x => x.Name).ShouldBe(["ConfirmAppointment", "ProposeAppointment", "IssueInvoice"]);

        // The declaration merged onto the derived slice rather than standing beside it.
        var confirm = model.Slices[0];
        confirm.HandlerType.ShouldBe(T<ConfirmAppointmentHandler>());
        confirm.Chapter.ShouldBe("BookingAppointments");
    }

    [Fact]
    public async Task an_explicit_name_is_still_a_separate_model()
    {
        using var services = Services(x => x
            .AddEventModelSource(new CodeDerivedSource())
            .AddEventModel<BookingAppointmentsModel>()
            .AddEventModel<SeparatelyNamedModel>());

        var models = await EventModelDiscovery.AssembleAsync(services);

        models.Select(x => x.Name).ShouldBe(["CritterCrush", "Reporting"]);
        models[1].Slices.ShouldHaveSingleItem().Name.ShouldBe("MonthlyReport");
    }

    [Fact]
    public async Task each_declared_slice_names_its_own_definition_as_its_origin()
    {
        using var services = Services(x => x
            .AddEventModel<BookingAppointmentsModel>()
            .AddEventModel<BillingModel>());

        var model = (await EventModelDiscovery.AssembleAsync(services)).ShouldHaveSingleItem();

        model.Slices.Single(x => x.Name == "ProposeAppointment").Origin
            .ShouldBe(new Uri("event-model://BookingAppointmentsModel"));
        model.Slices.Single(x => x.Name == "IssueInvoice").Origin
            .ShouldBe(new Uri("event-model://BillingModel"));
    }

    [Fact]
    public void an_unnamed_definition_instance_is_addressed_by_its_class_name()
    {
        EventModelDefinitionSource.For(new BillingModel()).Subject.ShouldBe(new Uri("event-model://BillingModel"));
    }

    [Fact]
    public async Task a_name_set_on_the_builder_still_wins()
    {
        using var services = Services(x => x.AddEventModel<BuilderNamedModel>());

        (await EventModelDiscovery.DiscoverAsync(services)).ShouldHaveSingleItem().Name.ShouldBe("FromBuilder");
    }

    public class BookingAppointmentsModel : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            model.InChapter("BookingAppointments");
            model.Command<ConfirmAppointment>();
            model.Command("ProposeAppointment");
        }
    }

    public class BillingModel : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model) => model.Command("IssueInvoice");
    }

    public class SeparatelyNamedModel : EventModelDefinition
    {
        public override string Name => "Reporting";
        public override void Configure(EventModelBuilder model) => model.View("MonthlyReport");
    }

    public class BuilderNamedModel : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            model.Name = "FromBuilder";
            model.Command("Anything");
        }
    }

    /// <summary>Stands in for Wolverine's source: Derived, and named for the service.</summary>
    private sealed class CodeDerivedSource : IEventModelDefinitionSource
    {
        public Uri Subject => new("event-model://wolverine");
        public EventModelProvenance Provenance => EventModelProvenance.Derived;

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
            => Task.FromResult<EventModelDescriptor?>(new EventModelDescriptor("CritterCrush",
            [
                EventModelSliceDescriptor.Named("ConfirmAppointment") with
                {
                    CommandType = T<ConfirmAppointment>(),
                    HandlerType = T<ConfirmAppointmentHandler>(),
                    Pattern = SlicePattern.Command,
                },
            ]));
    }

    public record ConfirmAppointment;
    public class ConfirmAppointmentHandler;
}
