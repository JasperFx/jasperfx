using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling.Fields;

/// <summary>
/// jasperfx#1299: typed field shapes on the Event Model — declared shapes, mapping, the merge rule and the
/// information-completeness question.
/// </summary>
public class FieldShapeTests
{
    private static TypeDescriptor T<TType>() => TypeDescriptor.For(typeof(TType));

    private static readonly JsonSerializerOptions CritterWatchWire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    #region sample_tests_field_shapes

    public record PlaceOrder;
    // The one stub that already has a member, so the view's mapping can point at it by lambda
    public record OrderPlaced(decimal OrderTotal);
    public record OrderLine;
    public class Order;
    public class OrderSummary;

    public class OrderShapes : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model)
        {
            model.Command<PlaceOrder>()
                .StartsStream<Order>()
                .Emits<OrderPlaced>();

            model.View<OrderSummary>().From<OrderPlaced>();

            // The fields the stubs will have, declared before they do
            model.Shape<PlaceOrder>()
                .Field("CustomerId").OfType<Guid>()
                .Field("OrderTotal").OfType<decimal>()
                .Field("Lines").OfType<OrderLine>().Many()
                .Field("Note").Optional();

            model.Shape<OrderPlaced>()
                .Field("OrderId").OfType<Guid>().Identity().Generated()
                .Field("CustomerId").OfType<Guid>().Pii()
                .Field("OrderTotal").OfType<decimal>().Describe("Sum of the lines, after discounts")
                .Field("Lines").OfType<OrderLine>().Many();

            model.Shape<OrderSummary>()
                .Field("Id").OfType<Guid>().Identity()
                // Named differently from the event: say where it comes from
                .Field("Total").OfType<decimal>().From<OrderPlaced>(x => x.OrderTotal)
                .Field("CustomerId").OfType<Guid>();
        }
    }

    #endregion

    private static EventModelDescriptor build()
    {
        var model = new EventModelBuilder();
        new OrderShapes().Configure(model);
        return model.Build("Orders");
    }

    // ---- declaring ----

    [Fact]
    public void a_shape_declares_fields_with_type_cardinality_flags_and_mapping()
    {
        var descriptor = build();

        descriptor.Shapes.Select(x => x.Type.Name).ShouldBe(["PlaceOrder", "OrderPlaced", "OrderSummary"]);

        var placed = descriptor.ShapeOf(T<OrderPlaced>())!;
        placed.Fields.Select(x => x.ToString()).ShouldBe(["OrderId: Guid", "CustomerId: Guid", "OrderTotal: Decimal", "Lines: OrderLine[]"]);
        placed.Find("orderid")!.Identity.ShouldBeTrue();
        placed.Find("OrderId")!.Generated.ShouldBeTrue();
        placed.Find("CustomerId")!.Pii.ShouldBeTrue();
        placed.Find("OrderTotal")!.Description.ShouldBe("Sum of the lines, after discounts");
        placed.Find("Lines")!.Cardinality.ShouldBe(FieldCardinality.Many);
        placed.Find("Missing").ShouldBeNull();

        var command = descriptor.ShapeOf(T<PlaceOrder>())!;
        command.Find("Note")!.ToString().ShouldBe("Note: String?");

        var summary = descriptor.ShapeOf(T<OrderSummary>())!;
        summary.Find("Total")!.Mapping.ShouldBe(new FieldMappingDescriptor(T<OrderPlaced>(), "OrderTotal"));
    }

    [Fact]
    public void a_field_declared_twice_is_one_field()
    {
        var model = new EventModelBuilder();
        model.Shape<OrderPlaced>().Field("Total").OfType<decimal>();
        model.Shape<OrderPlaced>().Field("total").Pii();

        var shape = model.Build("Orders").Shapes.ShouldHaveSingleItem();
        var field = shape.Fields.ShouldHaveSingleItem();
        field.Type.ShouldBe(T<decimal>());
        field.Pii.ShouldBeTrue();
    }

    [Fact]
    public void mapping_takes_a_string_form_and_a_declared_source()
    {
        var model = new EventModelBuilder();
        model.Shape("OrderSummary")
            .Field("Total").From<OrderPlaced>("OrderTotal")
            .Field("Customer").From("OrderPlaced", "CustomerId");

        var shape = model.Build("Orders").Shapes.ShouldHaveSingleItem();
        shape.Type.AssemblyName.ShouldBe("");
        shape.Find("Total")!.Mapping.ShouldBe(new FieldMappingDescriptor(T<OrderPlaced>(), "OrderTotal"));
        shape.Find("Customer")!.Mapping!.Source.Name.ShouldBe("OrderPlaced");
    }

    [Fact]
    public void a_field_tagged_by_a_tag_type_names_the_tag()
    {
        var model = new EventModelBuilder();
        model.Shape<OrderPlaced>().Field("CustomerId").Tagged<OrderSummary>();

        model.Build("Orders").Shapes.Single().Fields.Single().Tag.ShouldBe("orderSummary");
    }

    // ---- merge ----

    private static TypeShapeDescriptor shape<TType>(EventModelProvenance provenance, params FieldDescriptor[] fields)
        => new(T<TType>(), fields) { Provenance = provenance };

    private static FieldDescriptor field(string name) => new(name, T<string>());

    [Fact]
    public void a_declared_field_the_code_lacks_is_a_disagreement_and_an_extra_derived_field_is_not()
    {
        var declared = new EventModelDescriptor("Orders", [])
        {
            Shapes = [shape<OrderPlaced>(EventModelProvenance.Declared, field("OrderId"), field("Total"), field("Note"))],
        };
        var derived = new EventModelDescriptor("Orders", [])
        {
            Shapes = [shape<OrderPlaced>(EventModelProvenance.Derived, field("OrderId"), field("Total"), field("PlacedAt"))],
        };

        foreach (var merged in new[]
                 {
                     EventModelDescriptor.Merge("Orders", [declared, derived]),
                     EventModelDescriptor.Merge("Orders", [derived, declared]),
                 })
        {
            var shape = merged.Shapes.ShouldHaveSingleItem();
            shape.Provenance.ShouldBe(EventModelProvenance.Derived);
            // The code's fields win: the extra PlacedAt is kept and is not a finding...
            shape.Fields.Select(x => x.Name).ShouldBe(["OrderId", "Total", "PlacedAt"]);

            // ...and the declared Note the code lacks is
            var hotspot = merged.Hotspots.ShouldHaveSingleItem();
            hotspot.Origin.ShouldBe(HotspotOrigin.SourceDisagreement);
            hotspot.Role.ShouldBe(EventModelRole.Fields);
            hotspot.WinningClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Derived, "OrderId, Total, PlacedAt"));
            hotspot.LosingClaim.ShouldBe(new EventModelClaim(EventModelProvenance.Declared, "Note"));
            hotspot.Text.ShouldBe("Fields of OrderPlaced: Derived claims OrderId, Total, PlacedAt; Declared also declares Note, which Derived lacks");
        }
    }

    [Fact]
    public void declared_only_detail_travels_onto_the_derived_field()
    {
        var declared = new EventModelDescriptor("Orders", [])
        {
            Shapes = [shape<OrderSummary>(EventModelProvenance.Declared,
                field("Total") with { Mapping = new FieldMappingDescriptor(T<OrderPlaced>(), "OrderTotal"), Description = "Sum of the lines", Pii = true })],
        };
        var derived = new EventModelDescriptor("Orders", [])
        {
            Shapes = [shape<OrderSummary>(EventModelProvenance.Derived, new FieldDescriptor("Total", T<decimal>()))],
        };

        var merged = EventModelDescriptor.Merge("Orders", [declared, derived]).Shapes.Single().Fields.Single();

        merged.Type.ShouldBe(T<decimal>());
        merged.Mapping.ShouldBe(new FieldMappingDescriptor(T<OrderPlaced>(), "OrderTotal"));
        merged.Description.ShouldBe("Sum of the lines");
        merged.Pii.ShouldBeTrue();
    }

    [Fact]
    public void same_rung_shapes_union_by_field_name()
    {
        var first = shape<OrderPlaced>(EventModelProvenance.Declared, field("OrderId"), field("Total"));
        var second = shape<OrderPlaced>(EventModelProvenance.Declared, field("total") with { Pii = true }, field("Note"));

        var merged = first.Merge(second);
        merged.Fields.Select(x => x.Name).ShouldBe(["OrderId", "Total", "Note"]);
        merged.Find("Total")!.Pii.ShouldBeTrue();
    }

    [Fact]
    public void a_shape_declared_by_name_is_replaced_by_the_real_type()
    {
        var declared = new TypeShapeDescriptor(new TypeDescriptor("OrderPlaced", "OrderPlaced", ""), [field("Total")]) { Provenance = EventModelProvenance.Declared };
        var derived = shape<OrderPlaced>(EventModelProvenance.Derived, field("Total"));

        var merged = EventModelDescriptor.Merge("Orders", [
            new EventModelDescriptor("Orders", []) { Shapes = [declared] },
            new EventModelDescriptor("Orders", []) { Shapes = [derived] },
        ]);

        merged.Shapes.Single().Type.ShouldBe(T<OrderPlaced>());
        merged.Hotspots.ShouldBeEmpty();
    }

    [Fact]
    public void merging_shapes_of_different_types_throws()
    {
        Should.Throw<ArgumentException>(() =>
            shape<OrderPlaced>(EventModelProvenance.Declared).Merge(shape<PlaceOrder>(EventModelProvenance.Declared)));
    }

    [Fact]
    public void with_provenance_stamps_unattributed_shapes_and_leaves_attributed_ones()
    {
        var stamped = new EventModelDescriptor("Orders", [])
        {
            Shapes = [new TypeShapeDescriptor(T<OrderPlaced>(), []), shape<PlaceOrder>(EventModelProvenance.Observed)],
            Tags = [new EventTagDescriptor("orderId", null)],
        }.WithProvenance(EventModelProvenance.Derived);

        stamped.Shapes.Select(x => x.Provenance).ShouldBe([EventModelProvenance.Derived, EventModelProvenance.Observed]);
        stamped.Tags.Single().Provenance.ShouldBe(EventModelProvenance.Derived);
    }

    // ---- information completeness ----

    [Fact]
    public void a_declared_model_whose_fields_all_have_a_source_asks_nothing()
    {
        build().IncompleteInformation.ShouldBeEmpty();
    }

    [Fact]
    public void a_view_field_from_no_consumed_event_is_a_question()
    {
        var model = new EventModelBuilder();
        new OrderShapes().Configure(model);
        model.Shape<OrderSummary>().Field("ShippedAt").OfType<DateTimeOffset>();

        var descriptor = model.Build("Orders");
        var question = descriptor.IncompleteInformation.ShouldHaveSingleItem();

        question.Origin.ShouldBe(HotspotOrigin.IncompleteInformation);
        question.Role.ShouldBe(EventModelRole.Fields);
        question.SliceName.ShouldBe("OrderSummary");
        question.Field.ShouldBe("OrderSummary.ShippedAt");
        question.Text.ShouldBe("OrderSummary.ShippedAt comes from none of the events the view consumes (OrderPlaced). Map it with Field(\"ShippedAt\").From<…>(…), or consume the event that carries it");

        // Graded as a question, like Prose: it is on the model's computed list, never in a slice's Hotspots
        descriptor.Slices.SelectMany(x => x.Hotspots).ShouldBeEmpty();
        descriptor.IncompleteInformationFor(descriptor.Slices.Single(x => x.Name == "OrderSummary")).ShouldBe([question]);
    }

    [Fact]
    public void a_mapping_to_a_consumed_event_answers_the_question_and_a_mapping_elsewhere_does_not()
    {
        var model = new EventModelBuilder();
        model.View<OrderSummary>().From<OrderPlaced>();
        model.Shape<OrderPlaced>().Field("OrderTotal").OfType<decimal>();
        model.Shape<OrderSummary>()
            .Field("Total").From<OrderPlaced>(x => x.OrderTotal)
            .Field("Customer").From<PlaceOrder>("CustomerId");

        var question = model.Build("Orders").IncompleteInformation.ShouldHaveSingleItem();
        question.Field.ShouldBe("OrderSummary.Customer");
    }

    [Fact]
    public void a_mapping_to_a_field_the_source_shape_lacks_is_still_a_question()
    {
        var model = new EventModelBuilder();
        model.View<OrderSummary>().From<OrderPlaced>();
        model.Shape<OrderPlaced>().Field("OrderTotal").OfType<decimal>();
        model.Shape<OrderSummary>().Field("Total").From<OrderPlaced>("GrandTotal");

        model.Build("Orders").IncompleteInformation.ShouldHaveSingleItem().Field.ShouldBe("OrderSummary.Total");
    }

    [Fact]
    public void an_emitted_event_field_from_neither_command_aggregate_nor_generated_is_a_question()
    {
        var model = new EventModelBuilder();
        model.Command<PlaceOrder>().Against<Order>().Emits<OrderPlaced>();
        model.Shape<PlaceOrder>().Field("CustomerId");
        model.Shape<Order>().Field("Id").Identity();
        model.Shape<OrderPlaced>()
            .Field("Id")                       // from the aggregate
            .Field("CustomerId")               // from the command
            .Field("PlacedAt").Generated()     // generated, so exempt
            .Field("Total").From<Order>("Id")  // mapped onto the aggregate
            .Field("Channel");                 // from nowhere

        var question = model.Build("Orders").IncompleteInformation.ShouldHaveSingleItem();
        question.SliceName.ShouldBe("PlaceOrder");
        question.Field.ShouldBe("OrderPlaced.Channel");
        question.Text.ShouldBe("OrderPlaced.Channel comes from neither the command, the aggregate nor a generated value (PlaceOrder, Order). Map it with Field(\"Channel\").From<…>(…), mark it [Generated], or add it to the command");
    }

    [Fact]
    public void a_decider_model_counts_as_a_source()
    {
        var model = new EventModelBuilder();
        model.Command<PlaceOrder>().Decides<Order>().Emits<OrderPlaced>();
        model.Shape<Order>().Field("Total");
        model.Shape<OrderPlaced>().Field("Total");

        model.Build("Orders").IncompleteInformation.ShouldBeEmpty();
    }

    [Fact]
    public void a_purely_derived_model_gets_no_questions()
    {
        var derived = new EventModelDescriptor("Orders", [
            EventModelSliceDescriptor.Named("OrderSummary") with
            {
                Pattern = SlicePattern.View,
                ReadModelTypes = [T<OrderSummary>()],
                ConsumedEvents = [T<OrderPlaced>()],
            },
        ])
        {
            Shapes = [shape<OrderSummary>(EventModelProvenance.Derived, field("ShippedAt"))],
        }.WithProvenance(EventModelProvenance.Derived);

        derived.IncompleteInformation.ShouldBeEmpty();

        // The same slice with a declared claim on it is asked
        var declared = derived with
        {
            Slices = [derived.Slices[0].Merge(EventModelSliceDescriptor.Named("OrderSummary") with { Domain = "Sales", Provenance = EventModelProvenance.Declared })],
        };
        declared.Slices[0].HasRung(EventModelProvenance.Declared).ShouldBeTrue();
        declared.IncompleteInformation.ShouldHaveSingleItem();
    }

    [Fact]
    public void a_type_with_no_shape_is_not_asked_about()
    {
        var model = new EventModelBuilder();
        model.View<OrderSummary>().From<OrderPlaced>();

        model.Build("Orders").IncompleteInformation.ShouldBeEmpty();
    }

    // ---- wire ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void shapes_round_trip_and_the_questions_are_recomputed(bool critterWatchWire)
    {
        var options = critterWatchWire ? CritterWatchWire : new JsonSerializerOptions();
        var model = new EventModelBuilder();
        new OrderShapes().Configure(model);
        model.Shape<OrderSummary>().Field("ShippedAt").OfType<DateTimeOffset>().Optional();
        var descriptor = model.Build("Orders");

        var json = JsonSerializer.Serialize(descriptor, options);
        var back = JsonSerializer.Deserialize<EventModelDescriptor>(json, options)!;

        back.Shapes.ShouldBe(descriptor.Shapes);
        back.IncompleteInformation.ShouldBe(descriptor.IncompleteInformation);

        json.ShouldContain(critterWatchWire ? "\"incompleteInformation\":[" : "\"IncompleteInformation\":[");
        json.ShouldContain(critterWatchWire ? "\"sliceName\":\"OrderSummary\"" : "\"SliceName\":\"OrderSummary\"");
    }

    [Fact]
    public void hotspot_equality_includes_the_new_members()
    {
        var a = HotspotDescriptor.IncompleteInformation("S", "T", "F", "text");
        var b = HotspotDescriptor.IncompleteInformation("S", "T", "F", "text");
        var c = HotspotDescriptor.IncompleteInformation("S", "T", "G", "text");

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(c);
    }

    // ---- the manifest path ----

    [Fact]
    public async Task generated_shapes_attached_to_a_definition_source_land_on_the_derived_rung()
    {
        var source = EventModelDefinitionSource.For(new OrderShapes())
            .WithShapes([new TypeShapeDescriptor(T<OrderPlaced>(), [new FieldDescriptor("OrderId", T<Guid>()), new FieldDescriptor("OrderTotal", T<decimal>())])]);

        var descriptor = await ((IEventModelDefinitionSource)source).TryCreateAsync(
            new ServiceCollection().BuildServiceProvider(), default);

        var placed = descriptor!.ShapeOf(T<OrderPlaced>())!;
        placed.Provenance.ShouldBe(EventModelProvenance.Derived);
        placed.Fields.Select(x => x.Name).ShouldBe(["OrderId", "OrderTotal"]);
        // The declared Pii flag on CustomerId was on a field the code lacks, so that field is recorded...
        descriptor.Hotspots.ShouldHaveSingleItem().Text.ShouldStartWith("Fields of OrderPlaced:");
        // ...while the declared description travelled onto the derived OrderTotal
        placed.Find("OrderTotal")!.Description.ShouldBe("Sum of the lines, after discounts");
    }
}
