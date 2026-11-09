using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StjUnionSpike;

/// <summary>
/// Item 3 of #1037: discriminator vs structural. Shows what JSON lands in storage in each mode,
/// since that is what weasel#744's type-test SQL has to match.
/// </summary>
public class DiscriminatorVsStructuralTests
{
    private static object? Active(object? union) => ((IUnion)union!).Value;

    private static JsonSerializerOptions Structural()
    {
        var options = new JsonSerializerOptions();
        options.TypeClassifiers.Add(new JsonUnionTypeStructuralClassifier());
        return options;
    }

    // ------------------------------------------------------------------ structural

    [Fact]
    public void structural_classifier_selects_disjoint_cases_by_property_names()
    {
        var options = Structural();

        Active(JsonSerializer.Deserialize<Lifecycle>("""{"Owner":"ann"}""", options))
            .ShouldBe(new Opened("ann"));
        Active(JsonSerializer.Deserialize<Lifecycle>("""{"At":"2026-01-01T00:00:00+00:00"}""", options))
            .ShouldBe(new Closed(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Active(JsonSerializer.Deserialize<CartEvent>("""{"Id":"a","Quantity":2}""", options))
            .ShouldBe(new ItemAdded("a", 2));
        Active(JsonSerializer.Deserialize<CartEvent>("""{"Id":"a","Reason":"oops"}""", options))
            .ShouldBe(new ItemRemoved("a", "oops"));
    }

    [Fact]
    public void structural_classifier_follows_the_naming_policy()
    {
        var options = new JsonSerializerOptions(Structural()) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        JsonSerializer.Serialize(new Lifecycle(new Opened("ann")), options).ShouldBe("""{"owner":"ann"}""");
        Active(JsonSerializer.Deserialize<Lifecycle>("""{"owner":"ann"}""", options)).ShouldBe(new Opened("ann"));
    }

    [Fact]
    public void structural_classifier_fails_at_read_time_when_no_case_or_several_cases_fit()
    {
        var options = Structural();

        // Lifecycle's cases share no property name: {} and an unknown name fit neither, and a
        // payload carrying both cases' names fits neither exactly.
        foreach (var json in new[] { "{}", """{"Unknown":1}""", """{"Owner":"ann","At":"2026-01-01T00:00:00Z"}""" })
        {
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Lifecycle>(json, options))
                .Message.ShouldContain("returned null");
        }

        // CartEvent: only the shared property is present
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CartEvent>("""{"Id":"a"}""", options))
            .Message.ShouldContain("returned null");
    }

    [Fact]
    public void structural_classifier_refuses_the_union_up_front_when_one_case_shape_contains_another()
    {
        var options = Structural();

        // Not a per-payload failure: the classifier refuses to be built, so even a write throws
        // once the options try to build the union's contract.
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<Pet>("""{"Name":"Tom","Barks":1}""", options))
            .Message.ShouldContain("case type 'StjUnionSpike.Cat' can never be selected uniquely");
        Should.Throw<NotSupportedException>(() => JsonSerializer.Serialize(new Pet(new Cat("Tom")), options));
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<Length>("""{"Value":1}""", options));

        // The typical event union is refused the same way: Withdrawn(Amount, Reason) contains
        // Deposited(Amount). Event unions will often look like this.
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<AccountEvent>("""{"FrozenBy":"ops"}""", options))
            .Message.ShouldContain("case type 'StjUnionSpike.Deposited' can never be selected uniquely");
    }

    [Fact]
    public void required_members_on_the_larger_case_make_a_superset_shape_selectable()
    {
        var options = Structural();

        Active(JsonSerializer.Deserialize<YoungPet>("""{"Name":"Tom"}""", options)).ShouldBe(new Kitten("Tom"));
        Active(JsonSerializer.Deserialize<YoungPet>("""{"Name":"Rex","Barks":2}""", options))
            .ShouldBeOfType<Puppy>().Barks.ShouldBe(2);
    }

    [Fact]
    public void disallowing_unmapped_members_on_the_smaller_case_does_not_make_a_superset_shape_selectable()
    {
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<StrictPet>("""{"Name":"Tom"}""", Structural()))
            .Message.ShouldContain("can never be selected uniquely");
    }

    [Fact]
    public void structural_can_be_opted_into_per_type_with_JsonUnionAttribute()
    {
        Active(JsonSerializer.Deserialize<AttributedCartEvent>("""{"Id":"a","Reason":"oops"}"""))
            .ShouldBe(new ItemRemoved("a", "oops"));
    }

    [Fact]
    public void structural_classifier_still_handles_primitive_and_mixed_cases_by_token_kind()
    {
        var options = Structural();
        Active(JsonSerializer.Deserialize<IntOrString>("5", options)).ShouldBe(5);
        Active(JsonSerializer.Deserialize<NumberOrFlagOrList>("[3]", options)).ShouldBe(new[] { 3 });
    }

    // ------------------------------------------------------------------ discriminator

    [Fact]
    public void JsonPolymorphismOptions_are_refused_on_a_union_because_unions_are_structs()
    {
        // [JsonPolymorphic]/[JsonDerivedType] do not even compile on a union declaration (CS0592:
        // valid only on class and interface). Setting the same thing through a contract modifier
        // compiles, then fails at first use.
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    info =>
                    {
                        if (info.Type != typeof(AccountEvent)) return;
                        info.PolymorphismOptions = new JsonPolymorphismOptions
                        {
                            DerivedTypes =
                            {
                                new JsonDerivedType(typeof(Deposited), "deposited"),
                                new JsonDerivedType(typeof(Withdrawn), "withdrawn"),
                                new JsonDerivedType(typeof(Frozen), "frozen")
                            }
                        };
                    }
                }
            }
        };

        Should.Throw<InvalidOperationException>(() => JsonSerializer.Serialize(new AccountEvent(new Frozen("ops")), options))
            .Message.ShouldContain("does not support polymorphism");
    }

    [Fact]
    public void no_built_in_mode_writes_a_discriminator_for_a_union()
    {
        // Every mode above wrote the bare case. The only way to get a discriminator into the
        // stored JSON is for the case type to carry it (or a hand-written converter); a custom
        // classifier can then read it back.
        JsonSerializer.Serialize(new TaggedEvent(new TaggedWithdrawn(5m)))
            .ShouldBe("""{"$kind":"withdrawn","Amount":5}""");

        // Identical shapes, chosen only by the discriminator, regardless of its position
        Active(JsonSerializer.Deserialize<TaggedEvent>("""{"$kind":"withdrawn","Amount":5}"""))
            .ShouldBeOfType<TaggedWithdrawn>();
        Active(JsonSerializer.Deserialize<TaggedEvent>("""{"Amount":5,"$kind":"deposited"}"""))
            .ShouldBeOfType<TaggedDeposited>();
    }

    [Fact]
    public void a_class_hierarchy_keeps_its_discriminator_but_a_union_of_its_members_does_not()
    {
        // Polymorphism on a base type still writes "$type". A union over the same leaf types
        // writes the bare case. The two are separate features.
        JsonSerializer.Serialize<Shape>(new Circle(1)).ShouldBe("""{"$type":"circle","Radius":1}""");
        JsonSerializer.Serialize(new ShapeUnion(new Circle(1))).ShouldBe("""{"Radius":1}""");
    }
}

[JsonPolymorphic]
[JsonDerivedType(typeof(Circle), "circle")]
[JsonDerivedType(typeof(Square), "square")]
public abstract record Shape;
public record Circle(double Radius) : Shape;
public record Square(double Side) : Shape;
public union ShapeUnion(Circle, Square);
