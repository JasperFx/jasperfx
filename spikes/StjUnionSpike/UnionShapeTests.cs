using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace StjUnionSpike;

/// <summary>
/// What a C# 15 union IS at runtime, and how code that only sees `object` or `Type` can recognize
/// one and reach its active case. This is the detection/unwrap seam #1053 needs.
/// </summary>
public class UnionShapeTests
{
    [Fact]
    public void a_keyword_union_is_a_struct_marked_Union_that_implements_IUnion()
    {
        var type = typeof(Pet);

        type.IsValueType.ShouldBeTrue();
        type.GetCustomAttribute<UnionAttribute>().ShouldNotBeNull();
        typeof(IUnion).IsAssignableFrom(type).ShouldBeTrue();

        // One public constructor per case, and the IUnion.Value property. Nothing else: no
        // TryGetValue, no Cases list, no tag. The case list has to come from the constructors
        // or from System.Text.Json's metadata.
        type.GetConstructors().Select(c => c.GetParameters().Single().ParameterType)
            .ShouldBe([typeof(Cat), typeof(Dog)], ignoreOrder: true);
    }

    [Fact]
    public void boxed_to_object_a_union_is_IUnion_and_Value_is_the_active_case()
    {
        object boxed = new Pet(new Dog("Rex", 3));

        (boxed is IUnion).ShouldBeTrue();
        ((IUnion)boxed).Value.ShouldBe(new Dog("Rex", 3));
    }

    [Fact]
    public void pattern_matching_a_statically_typed_union_tests_the_CASE_not_the_union()
    {
        var pet = new Pet(new Dog("Rex", 3));

        // C# union patterns look through the union to its contents. `pet is IUnion` asks
        // "is the Dog an IUnion?", and the answer is no. Detection code must work on `object`
        // (or Type), never on a variable typed as the union.
        (pet is IUnion).ShouldBeFalse();
        (pet is Dog).ShouldBeTrue();
        (pet is Cat).ShouldBeFalse();
    }

    private static bool IsUnion<T>(T value) => value is IUnion;

    [Fact]
    public void through_a_generic_type_parameter_the_union_itself_is_tested()
    {
        // Generic event paths (Append<T>, codegen over T @event) see the union, not the case
        IsUnion(new Pet(new Dog("Rex", 3))).ShouldBeTrue();
        IsUnion(new Dog("Rex", 3)).ShouldBeFalse();
    }

    [Fact]
    public void the_default_union_value_has_a_null_Value()
    {
        object boxed = default(Pet);
        ((IUnion)boxed).Value.ShouldBeNull();
    }

    [Fact]
    public void a_hand_written_Union_type_is_recognized_by_STJ_exactly_like_a_keyword_union()
    {
        var info = JsonSerializerOptions.Default.GetTypeInfo(typeof(HandWrittenPet));

        info.Kind.ShouldBe(JsonTypeInfoKind.Union);
        info.UnionCases.Select(c => c.CaseType).ShouldBe([typeof(Cat), typeof(Dog)], ignoreOrder: true);
        JsonSerializer.Serialize(new HandWrittenPet(new Cat("Tom"))).ShouldBe("""{"Name":"Tom"}""");
    }

    [Fact]
    public void STJ_metadata_lists_the_cases_and_deconstructs_without_IUnion()
    {
        var info = JsonSerializerOptions.Default.GetTypeInfo(typeof(AccountEvent));

        info.Kind.ShouldBe(JsonTypeInfoKind.Union);
        info.UnionCases.Select(c => c.CaseType)
            .ShouldBe([typeof(Deposited), typeof(Withdrawn), typeof(Frozen)]);

        var (caseType, value) = info.UnionDeconstructor!(new AccountEvent(new Frozen("ops")));
        caseType.ShouldBe(typeof(Frozen));
        value.ShouldBe(new Frozen("ops"));

        var rebuilt = info.UnionConstructor!(typeof(Deposited), new Deposited(5m));
        rebuilt.ShouldBeOfType<AccountEvent>();
        ((IUnion)rebuilt).Value.ShouldBe(new Deposited(5m));
    }

    [Fact]
    public void a_nullable_case_is_reported_as_nullable()
    {
        var info = JsonSerializerOptions.Default.GetTypeInfo(typeof(MaybeCat));

        info.UnionCases.Single(c => c.CaseType == typeof(Cat)).IsNullable.ShouldBeTrue();
        info.UnionCases.Single(c => c.CaseType == typeof(Dog)).IsNullable.ShouldBeFalse();
    }
}
