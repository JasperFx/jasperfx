using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StjUnionSpike;

/// <summary>
/// Item 2 of #1037: the System.Text.Json source generator. It emits union metadata
/// (JsonMetadataServices.CreateUnionInfo) and behaves exactly like reflection.
///
/// Building this project shows SYSLIB1227 for <see cref="AccountEvent"/> in
/// <see cref="DefaultContext"/>: at compile time, the generator warns that every case is a JSON
/// object and no classifier is set. Reflection reports the same problem only at run time.
/// </summary>
public class SourceGenerationTests
{
    private static object? Active(object? union) => ((IUnion)union!).Value;

    [Fact]
    public void the_generator_produces_union_metadata()
    {
        var info = DefaultContext.Default.AccountEvent;

        info.Kind.ShouldBe(JsonTypeInfoKind.Union);
        info.UnionCases.Select(c => c.CaseType)
            .ShouldBe([typeof(Deposited), typeof(Withdrawn), typeof(Frozen)]);
        info.UnionDeconstructor!(new AccountEvent(new Deposited(1m))).ShouldBe((typeof(Deposited), new Deposited(1m)));
    }

    [Fact]
    public void writing_matches_reflection()
    {
        JsonSerializer.Serialize(new AccountEvent(new Withdrawn(5m, "atm")), DefaultContext.Default.AccountEvent)
            .ShouldBe(JsonSerializer.Serialize(new AccountEvent(new Withdrawn(5m, "atm"))));

        JsonSerializer.Serialize(new IntOrString("x"), DefaultContext.Default.IntOrString).ShouldBe("\"x\"");
    }

    [Fact]
    public void reading_object_cases_with_the_default_classifier_fails_as_with_reflection()
    {
        Should.Throw<JsonException>(() =>
                JsonSerializer.Deserialize("""{"Amount":5}""", DefaultContext.Default.AccountEvent))
            .Message.ShouldContain("is ambiguous for union type");
    }

    [Fact]
    public void token_kind_classification_works_without_configuration()
    {
        Active(JsonSerializer.Deserialize("\"x\"", DefaultContext.Default.IntOrString)).ShouldBe("x");
        Active(JsonSerializer.Deserialize("7", DefaultContext.Default.IntOrString)).ShouldBe(7);
    }

    [Fact]
    public void the_structural_classifier_can_be_set_for_the_whole_context()
    {
        Active(JsonSerializer.Deserialize("""{"Id":"a","Quantity":2}""", StructuralContext.Default.CartEvent))
            .ShouldBe(new ItemAdded("a", 2));
        Active(JsonSerializer.Deserialize("""{"Id":"a","Reason":"oops"}""", StructuralContext.Default.CartEvent))
            .ShouldBe(new ItemRemoved("a", "oops"));
    }

    [Fact]
    public void the_JsonUnion_attribute_classifier_is_honoured_by_the_generator()
    {
        Active(JsonSerializer.Deserialize("""{"Id":"a","Reason":"oops"}""", DefaultContext.Default.AttributedCartEvent))
            .ShouldBe(new ItemRemoved("a", "oops"));

        Active(JsonSerializer.Deserialize("""{"Amount":5,"$kind":"withdrawn"}""", DefaultContext.Default.TaggedEvent))
            .ShouldBeOfType<TaggedWithdrawn>();
    }

    [Fact]
    public void a_union_nested_in_a_document_works_through_the_context()
    {
        var json = JsonSerializer.Serialize(
            new CartSnapshot(Guid.Empty, new CartEvent(new ItemRemoved("a", "oops"))), StructuralContext.Default.CartSnapshot);

        var snapshot = JsonSerializer.Deserialize(json, StructuralContext.Default.CartSnapshot)!;
        Active(snapshot.LastEvent).ShouldBe(new ItemRemoved("a", "oops"));
    }
}

[JsonSerializable(typeof(AccountEvent))]
[JsonSerializable(typeof(AttributedCartEvent))]
[JsonSerializable(typeof(IntOrString))]
[JsonSerializable(typeof(TaggedEvent))]
public partial class DefaultContext : JsonSerializerContext;

[JsonSourceGenerationOptions(TypeClassifiers = [typeof(JsonUnionTypeStructuralClassifier)])]
[JsonSerializable(typeof(CartEvent))]
[JsonSerializable(typeof(CartSnapshot))]
public partial class StructuralContext : JsonSerializerContext;
