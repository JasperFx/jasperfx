using System.Runtime.CompilerServices;
using System.Text.Json;

namespace StjUnionSpike;

/// <summary>
/// Item 1 of #1037: reflection-based serialization with default options (no classifier).
/// </summary>
public class ReflectionSerializationTests
{
    private static object? Active(object union) => ((IUnion)union).Value;

    [Fact]
    public void writing_emits_the_active_case_alone_with_no_wrapper_and_no_discriminator()
    {
        JsonSerializer.Serialize(new Pet(new Cat("Tom"))).ShouldBe("""{"Name":"Tom"}""");
        JsonSerializer.Serialize(new Pet(new Dog("Rex", 3))).ShouldBe("""{"Name":"Rex","Barks":3}""");
        JsonSerializer.Serialize(new IntOrString(5)).ShouldBe("5");
        JsonSerializer.Serialize(new IntOrString("five")).ShouldBe("\"five\"");
    }

    [Fact]
    public void writing_the_default_union_emits_null()
    {
        JsonSerializer.Serialize(default(Pet)).ShouldBe("null");
    }

    [Fact]
    public void writing_a_union_boxed_as_object_still_emits_the_case()
    {
        JsonSerializer.Serialize<object>(new Pet(new Dog("Rex", 1))).ShouldBe("""{"Name":"Rex","Barks":1}""");
    }

    [Fact]
    public void writing_a_union_nested_in_a_document_inlines_the_case()
    {
        JsonSerializer.Serialize(new Adoption("North", new Pet(new Dog("Rex", 1))))
            .ShouldBe("""{"Shelter":"North","Pet":{"Name":"Rex","Barks":1}}""");
    }

    [Fact]
    public void the_default_classifier_selects_a_case_by_JSON_token_kind_only()
    {
        Active(JsonSerializer.Deserialize<IntOrString>("5")).ShouldBe(5);
        Active(JsonSerializer.Deserialize<IntOrString>("\"five\"")).ShouldBe("five");

        Active(JsonSerializer.Deserialize<NumberOrFlagOrList>("1.5")).ShouldBe(1.5);
        Active(JsonSerializer.Deserialize<NumberOrFlagOrList>("true")).ShouldBe(true);
        Active(JsonSerializer.Deserialize<NumberOrFlagOrList>("[1,2]")).ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public void a_token_kind_no_case_accepts_is_rejected()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<IntOrString>("true"))
            .Message.ShouldContain("JSON token type 'True' is not supported for union type");
    }

    [Fact]
    public void json_null_reads_as_the_default_union()
    {
        Active(JsonSerializer.Deserialize<Pet>("null")).ShouldBeNull();
        Active(JsonSerializer.Deserialize<MaybeCat>("null")).ShouldBeNull();
    }

    [Fact]
    public void two_object_cases_cannot_be_read_back_without_a_classifier()
    {
        // Round trip fails: writing works (above), reading refuses because both cases are objects.
        // This is true whether the shapes overlap (Pet), are identical (Length) or are disjoint
        // (AccountEvent). The default classifier never looks at property names.
        foreach (var (type, json) in new (Type, string)[]
                 {
                     (typeof(Pet), """{"Name":"Tom"}"""),
                     (typeof(Length), """{"Value":1}"""),
                     (typeof(AccountEvent), """{"FrozenBy":"ops"}"""),
                     (typeof(Adoption), """{"Shelter":"North","Pet":{"Name":"Rex","Barks":1}}""")
                 })
        {
            Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, type))
                .Message.ShouldContain("is ambiguous for union type");
        }
    }
}
