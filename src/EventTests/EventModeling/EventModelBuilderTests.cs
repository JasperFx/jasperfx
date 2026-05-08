using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace EventTests.EventModeling;

public class EventModelBuilderTests
{
    [Fact]
    public void smoke_test_exercises_every_chain_method()
    {
        var definition = new SampleDefinition();
        var builder = new EventModelBuilder { Name = "Sample" };

        definition.Configure(builder);

        var slices = builder.BuildSlices();
        slices.Count.ShouldBe(1);

        var slice = slices[0];
        slice.Name.ShouldBe("Place order");
        slice.TriggerLabel.ShouldBe("User clicks Place Order");
        slice.TriggerType.ShouldBe(TypeDescriptor.For(typeof(SampleTrigger)));
        slice.CommandType.ShouldBe(TypeDescriptor.For(typeof(SampleCommand)));
        slice.HandlerType.ShouldBe(TypeDescriptor.For(typeof(SampleHandler)));
        slice.EmittedEvents.ShouldContain(TypeDescriptor.For(typeof(SampleEvent)));
        slice.ProjectionTypes.ShouldContain(TypeDescriptor.For(typeof(SampleProjection)));
        slice.ReadModelTypes.ShouldContain(TypeDescriptor.For(typeof(SampleReadModel)));
    }

    [Fact]
    public void multiple_slices_are_kept_in_declaration_order()
    {
        var builder = new EventModelBuilder();
        builder.Slice("first").Command<SampleCommand>();
        builder.Slice("second").Command<SampleOtherCommand>();

        var slices = builder.BuildSlices();

        slices.Select(s => s.Name).ShouldBe(new[] { "first", "second" });
    }

    [Fact]
    public void emits_supports_multiple_event_types()
    {
        var builder = new EventModelBuilder();
        builder.Slice("multi")
            .Emits<SampleEvent>()
            .Emits<SampleOtherEvent>();

        var slice = builder.BuildSlices().Single();
        slice.EmittedEvents.Count.ShouldBe(2);
    }

    [Fact]
    public void event_model_descriptor_round_trips_through_builder()
    {
        var builder = new EventModelBuilder { Name = "Sample" };
        builder.Slice("x").Command<SampleCommand>().Emits<SampleEvent>();

        var descriptor = new EventModelDescriptor(builder.Name!, builder.BuildSlices());

        descriptor.Name.ShouldBe("Sample");
        descriptor.Slices.Count.ShouldBe(1);
        descriptor.Slices[0].EmittedEvents.Count.ShouldBe(1);
    }

    public class SampleDefinition : EventModelDefinition
    {
        public override void Configure(EventModelBuilder builder)
        {
            builder.Slice("Place order")
                .TriggeredBy("User clicks Place Order")
                .TriggeredBy<SampleTrigger>()
                .Command<SampleCommand>()
                .HandledBy<SampleHandler>()
                .Emits<SampleEvent>()
                .Projects<SampleProjection>()
                .Reads<SampleReadModel>();
        }
    }

    public class SampleTrigger { }
    public class SampleCommand { }
    public class SampleOtherCommand { }
    public class SampleHandler { }
    public class SampleEvent { }
    public class SampleOtherEvent { }
    public class SampleProjection { }
    public class SampleReadModel { }
}
