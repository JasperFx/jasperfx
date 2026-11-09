using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StjUnionSpike;

// ---- Object cases whose shapes overlap: Dog's property names are a superset of Cat's ----
public record Cat(string Name);
public record Dog(string Name, int Barks);
public union Pet(Cat, Dog);

// ---- Object cases with identical shapes ----
public record Meters(int Value);
public record Feet(int Value);
public union Length(Meters, Feet);

// ---- A typical event union: Withdrawn's property names are a superset of Deposited's ----
public record Deposited(decimal Amount);
public record Withdrawn(decimal Amount, string Reason);
public record Frozen(string FrozenBy);
public union AccountEvent(Deposited, Withdrawn, Frozen);

// ---- Object cases with disjoint property names ----
public record Opened(string Owner);
public record Closed(DateTimeOffset At);
public union Lifecycle(Opened, Closed);

// ---- Cases sharing one property (an id), each with a distinct one ----
public record ItemAdded(string Id, int Quantity);
public record ItemRemoved(string Id, string Reason);
public union CartEvent(ItemAdded, ItemRemoved);

// ---- Superset shape made selectable by `required` on the larger case ----
public record Kitten(string Name);
public record Puppy
{
    public required string Name { get; init; }
    public required int Barks { get; init; }
}
public union YoungPet(Kitten, Puppy);

// ---- Superset shape with the SMALLER case refusing unknown members: still refused on RC3 ----
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record StrictCat(string Name);
public union StrictPet(StrictCat, Dog);

// ---- Primitive cases: classified by JSON token kind ----
public union IntOrString(int, string);
public union NumberOrFlagOrList(double, bool, int[]);

// ---- A nullable case ----
public union MaybeCat(Cat?, Dog);

// ---- Opt in to the structural classifier per type, by attribute ----
[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]
public union AttributedCartEvent(ItemAdded, ItemRemoved);

// ---- Discriminator written by the case types themselves, read by a custom classifier ----
public record TaggedDeposited(decimal Amount)
{
    [JsonPropertyName("$kind"), JsonPropertyOrder(-1)] public string Kind => "deposited";
}

public record TaggedWithdrawn(decimal Amount)
{
    [JsonPropertyName("$kind"), JsonPropertyOrder(-1)] public string Kind => "withdrawn";
}

[JsonUnion(TypeClassifier = typeof(KindPropertyClassifier))]
public union TaggedEvent(TaggedDeposited, TaggedWithdrawn);

/// <summary>
/// Reads a "$kind" property at the top level of the object to choose a case. A JsonTypeClassifier
/// only reads: it takes the reader and returns a Type. Writing the discriminator is the case
/// types' job (see TaggedDeposited) or a converter's.
/// </summary>
public class KindPropertyClassifier : JsonTypeClassifierFactory
{
    public override bool CanClassify(JsonTypeClassifierContext context)
        => context.Kind == JsonTypeClassifierKind.Union;

    public override JsonTypeClassifier CreateJsonClassifier(JsonTypeClassifierContext context, JsonSerializerOptions options)
    {
        var byName = context.UnionCases.ToDictionary(
            c => c.CaseType.Name.Replace("Tagged", "").ToLowerInvariant(), c => c.CaseType);

        return (ref Utf8JsonReader reader) =>
        {
            if (reader.TokenType != JsonTokenType.StartObject) return null;

            // The reader is a struct: classify on a copy so the real one is not advanced
            var copy = reader;
            var depth = copy.CurrentDepth;
            while (copy.Read() && copy.CurrentDepth > depth)
            {
                if (copy.TokenType == JsonTokenType.PropertyName && copy.ValueTextEquals("$kind"))
                {
                    copy.Read();
                    return byName.GetValueOrDefault(copy.GetString()!);
                }

                if (copy.TokenType == JsonTokenType.PropertyName)
                {
                    copy.Read();
                    copy.Skip();
                }
            }

            return null;
        };
    }
}

// ---- A hand-written union: [Union] + IUnion, no `union` keyword ----
[Union]
public readonly struct HandWrittenPet : IUnion
{
    public HandWrittenPet(Cat cat) => Value = cat;
    public HandWrittenPet(Dog dog) => Value = dog;
    public object? Value { get; }
}

// ---- A union nested in a document ----
public record Adoption(string Shelter, Pet Pet);
public record CartSnapshot(Guid Id, CartEvent LastEvent);

// ---- An aggregate whose Apply takes a union, for the Apply(union) discovery question ----
public class Account
{
    public decimal Balance { get; private set; }
    public bool IsFrozen { get; private set; }

    public void Apply(AccountEvent e)
    {
        switch (e)
        {
            case Deposited d: Balance += d.Amount; break;
            case Withdrawn w: Balance -= w.Amount; break;
            case Frozen: IsFrozen = true; break;
        }
    }
}
