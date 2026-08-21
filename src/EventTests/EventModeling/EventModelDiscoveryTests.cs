using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

// jasperfx#687 §3: the discovery / registration bridge. Before this, IEventModelDefinitionSource
// had zero implementations anywhere.
public class EventModelDiscoveryTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    [Fact]
    public void a_definition_type_becomes_a_source_with_an_event_model_subject()
    {
        var source = EventModelDefinitionSource.For<OrdersOverlay>();
        source.Subject.ShouldBe(new Uri("event-model://OrdersOverlay"));
    }

    [Fact]
    public void a_named_definition_is_addressed_by_its_name()
    {
        EventModelDefinitionSource.For(new NamedOverlay()).Subject.ShouldBe(new Uri("event-model://Orders"));
        EventModelDefinitionSource.For("Orders", _ => { }).Subject.ShouldBe(new Uri("event-model://Orders"));
    }

    [Fact]
    public void an_abstract_or_unrelated_type_is_rejected()
    {
        Should.Throw<ArgumentException>(() => EventModelDefinitionSource.For(typeof(EventModelDefinition)));
        Should.Throw<ArgumentException>(() => EventModelDefinitionSource.For(typeof(string)));
    }

    [Fact]
    public async Task a_subclass_is_instantiated_configured_and_snapshotted()
    {
        var services = new ServiceCollection().AddEventModel<OrdersOverlay>().BuildServiceProvider();

        var descriptors = await EventModelDiscovery.DiscoverAsync(services);

        var model = descriptors.Single();
        model.Name.ShouldBe("OrdersOverlay");
        model.Slices.Single().Name.ShouldBe("PlaceOrder");
        model.Slices.Single().Domain.ShouldBe("Orders");
    }

    [Fact]
    public async Task a_subclass_with_dependencies_gets_them_from_di()
    {
        var services = new ServiceCollection()
            .AddSingleton(new SliceNames("FromDi"))
            .AddEventModel(typeof(DependentOverlay))
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.DiscoverAsync(services)).Single();
        model.Slices.Single().Name.ShouldBe("FromDi");
    }

    [Fact]
    public async Task a_subclass_registered_in_di_is_resolved_rather_than_constructed()
    {
        var instance = new OrdersOverlay();
        var services = new ServiceCollection()
            .AddSingleton(instance)
            .AddEventModel<OrdersOverlay>()
            .BuildServiceProvider();

        await EventModelDiscovery.DiscoverAsync(services);
        instance.Configured.ShouldBe(1);
    }

    [Fact]
    public async Task an_inline_lambda_is_a_source_too()
    {
        var services = new ServiceCollection()
            .AddEventModel("Orders", model => model.Slice("PlaceOrder").TriggeredBy("User clicks Place Order"))
            .BuildServiceProvider();

        var model = (await EventModelDiscovery.DiscoverAsync(services)).Single();
        model.Name.ShouldBe("Orders");
        model.Slices.Single().TriggerLabel.ShouldBe("User clicks Place Order");
    }

    [Fact]
    public async Task an_instance_is_a_source_too()
    {
        var services = new ServiceCollection().AddEventModel(new NamedOverlay()).BuildServiceProvider();
        (await EventModelDiscovery.DiscoverAsync(services)).Single().Name.ShouldBe("Orders");
    }

    [Fact]
    public async Task every_concrete_definition_in_an_assembly_can_be_registered()
    {
        var services = new ServiceCollection()
            .AddSingleton(new SliceNames("x"))
            .AddEventModelsFromAssembly(typeof(EventModelDiscoveryTests).Assembly)
            .BuildServiceProvider();

        var names = (await EventModelDiscovery.DiscoverAsync(services)).Select(x => x.Name).ToList();
        names.ShouldContain("OrdersOverlay");
        names.ShouldContain("Orders");       // NamedOverlay
        names.ShouldContain("DependentOverlay");
        names.ShouldNotContain(nameof(AbstractOverlay));
    }

    [Fact]
    public async Task a_source_that_returns_null_is_skipped()
    {
        var services = new ServiceCollection()
            .AddEventModelSource(new NullSource())
            .AddEventModel<OrdersOverlay>()
            .BuildServiceProvider();

        (await EventModelDiscovery.DiscoverAsync(services)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task assemble_folds_the_overlay_onto_a_derived_source_by_model_and_slice_name()
    {
        // A derived source — what Wolverine (wolverine#3988) or the Bobcat generator (bobcat#106)
        // will register — first, then the overlay. Registration order is merge order.
        var services = new ServiceCollection()
            .AddEventModelSource(new DerivedSource())
            .AddEventModel<OrdersOverlay>()            // model name "OrdersOverlay" — a second model
            .AddEventModel("Orders", model =>          // model name "Orders" — folds onto the derived one
            {
                model.Slice("PlaceOrder").InDomain("Orders").TriggeredBy("User clicks Place Order");
                model.Slice("CancelOrder").InDomain("Orders");
            })
            .BuildServiceProvider();

        var models = await EventModelDiscovery.AssembleAsync(services);

        models.Select(m => m.Name).ShouldBe(new[] { "Orders", "OrdersOverlay" });

        var orders = models[0];
        orders.Slices.Select(s => s.Name).ShouldBe(new[] { "PlaceOrder", "CancelOrder" });
        var place = orders.Slices[0];
        place.CommandType.ShouldBe(T<PlaceOrder>());                // derived role survives
        place.Pattern.ShouldBe(SlicePattern.Command);
        place.TriggerLabel.ShouldBe("User clicks Place Order");     // overlay annotation lands
        place.Domain.ShouldBe("Orders");
        place.Specifications.Single().Identity.ShouldBe("Place Order/Place an order");
        orders.Slices[1].CommandType.ShouldBeNull();                // overlay-only slice: named, not derived
        orders.Aggregates.Single().Type.ShouldBe(T<Order>());
    }

    public class OrdersOverlay : EventModelDefinition
    {
        public int Configured { get; private set; }

        public override void Configure(EventModelBuilder builder)
        {
            Configured++;
            builder.InDomain("Orders");
            builder.Slice("PlaceOrder");
        }
    }

    public class NamedOverlay : EventModelDefinition
    {
        public override string Name => "Orders";
        public override void Configure(EventModelBuilder builder) => builder.Slice("ShipOrder");
    }

    public record SliceNames(string Name);

    public class DependentOverlay : EventModelDefinition
    {
        private readonly SliceNames _names;
        public DependentOverlay(SliceNames names) => _names = names;
        public override void Configure(EventModelBuilder builder) => builder.Slice(_names.Name);
    }

    public abstract class AbstractOverlay : EventModelDefinition
    {
    }

    private sealed class NullSource : IEventModelDefinitionSource
    {
        public Uri Subject => new("event-model://null");
        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
            => Task.FromResult<EventModelDescriptor?>(null);
    }

    private sealed class DerivedSource : IEventModelDefinitionSource
    {
        public Uri Subject => new("event-model://derived");

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
            => Task.FromResult<EventModelDescriptor?>(new EventModelDescriptor("Orders", new[]
            {
                new EventModelSliceDescriptor("PlaceOrder", null, null, T<PlaceOrder>(), T<OrderHandler>(),
                    new[] { T<OrderPlaced>() }, Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>())
                {
                    Pattern = SlicePattern.Command,
                    TriggerKind = TriggerKind.Http,
                    AggregateTypes = new[] { T<Order>() },
                    Specifications = new[] { new SpecificationDescriptor("Place Order/Place an order", new[] { T<PlaceOrder>(), T<OrderPlaced>() }) },
                },
            })
            {
                Aggregates = new[] { new AggregateDescriptor(T<Order>(), AggregateKind.WriteAggregate, new[] { T<OrderPlaced>() }) },
            });
    }

    public class PlaceOrder { }
    public class OrderHandler { }
    public class Order { }
    public class OrderPlaced { }
}
