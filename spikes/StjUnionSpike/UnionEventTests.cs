using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JasperFx.Events;

namespace StjUnionSpike;

/// <summary>
/// Item 4 of #1037: unions as events. What JasperFx.Events does with a union today, and evidence
/// for the design #1053 proposes: Append unwraps to the active case, and Apply(union) methods are
/// discovered as handlers for every case.
///
/// The helpers here (<see cref="Unwrap"/>, <see cref="UnionCases"/>) are spike code that shows the
/// mechanism. They are not a proposed API.
/// </summary>
public class UnionEventTests
{
    /// <summary>
    /// Unwrap a union to its active case, recursively, working on object (see
    /// UnionShapeTests.pattern_matching_a_statically_typed_union_tests_the_CASE_not_the_union).
    /// </summary>
    public static object Unwrap(object @event)
    {
        while (@event is IUnion union)
        {
            @event = union.Value
                     ?? throw new ArgumentException($"The {@event.GetType().Name} union has no active case (default value)");
        }

        return @event;
    }

    /// <summary>
    /// Case types of a union using reflection alone: one single-parameter public constructor per
    /// case. No serializer options are needed, so this can run in projection discovery.
    /// </summary>
    public static Type[] UnionCases(Type type)
        => type.GetCustomAttribute<UnionAttribute>() == null
            ? []
            : type.GetConstructors()
                .Select(c => c.GetParameters())
                .Where(p => p.Length == 1)
                .Select(p => p[0].ParameterType)
                .ToArray();

    [Fact]
    public void today_a_union_event_is_named_and_typed_as_the_union_not_the_case()
    {
        object appended = new AccountEvent(new Deposited(5m));

        // Without unwrapping, every case of the union would share one event type name, and the
        // stored JSON would carry no indication of which case it holds.
        appended.GetType().GetEventTypeName().ShouldBe("account_event");

        var wrapped = new Event<AccountEvent>(new AccountEvent(new Deposited(5m)));
        wrapped.EventType.ShouldBe(typeof(AccountEvent));
    }

    [Fact]
    public void unwrapping_gives_the_case_its_own_event_type_name_and_type()
    {
        var unwrapped = Unwrap(new AccountEvent(new Deposited(5m)));

        unwrapped.ShouldBe(new Deposited(5m));
        unwrapped.GetType().GetEventTypeName().ShouldBe("deposited");
        Unwrap(new AccountEvent(new Frozen("ops"))).GetType().GetEventTypeName().ShouldBe("frozen");
    }

    [Fact]
    public void unwrapping_leaves_a_plain_event_alone_and_rejects_a_default_union()
    {
        Unwrap(new Deposited(1m)).ShouldBe(new Deposited(1m));
        Should.Throw<ArgumentException>(() => Unwrap(default(AccountEvent)));
    }

    [Fact]
    public void unwrapped_case_json_reads_back_as_the_case_with_plain_options()
    {
        // The event store knows the case type from the event type name, so it never needs to
        // deserialize into the union. No classifier is required, even for unions the default
        // classifier cannot read (every case here is an object).
        var stored = JsonSerializer.Serialize(Unwrap(new AccountEvent(new Withdrawn(5m, "atm"))));
        stored.ShouldBe("""{"Amount":5,"Reason":"atm"}""");

        JsonSerializer.Deserialize(stored, typeof(Withdrawn)).ShouldBe(new Withdrawn(5m, "atm"));
    }

    [Fact]
    public void serializing_the_union_or_the_unwrapped_case_stores_the_same_json()
    {
        // So unwrapping changes the event type name and the IEvent's type, not the stored data.
        var union = new AccountEvent(new Frozen("ops"));
        JsonSerializer.Serialize(union).ShouldBe(JsonSerializer.Serialize(Unwrap(union)));
    }

    [Fact]
    public void reading_stored_case_json_back_as_the_union_needs_a_classifier_and_may_be_impossible()
    {
        var options = new JsonSerializerOptions();
        options.TypeClassifiers.Add(new System.Text.Json.Serialization.JsonUnionTypeStructuralClassifier());

        // Disjoint cases: the structural classifier can do it
        var cart = """{"Id":"a","Reason":"oops"}""";
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<CartEvent>(cart));
        ((IUnion)JsonSerializer.Deserialize<CartEvent>(cart, options)).Value.ShouldBe(new ItemRemoved("a", "oops"));

        // Overlapping cases (Withdrawn contains Deposited): no built-in classifier can. Only the
        // event type name, which the store has and the JSON does not, says which case it is.
        var account = """{"FrozenBy":"ops"}""";
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<AccountEvent>(account));
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<AccountEvent>(account, options));
    }

    [Fact]
    public void the_case_list_from_reflection_matches_the_one_from_STJ()
    {
        UnionCases(typeof(AccountEvent))
            .ShouldBe(JsonSerializerOptions.Default.GetTypeInfo(typeof(AccountEvent)).UnionCases.Select(c => c.CaseType),
                ignoreOrder: true);

        UnionCases(typeof(HandWrittenPet)).ShouldBe([typeof(Cat), typeof(Dog)], ignoreOrder: true);
        UnionCases(typeof(Deposited)).ShouldBeEmpty();
    }

    [Fact]
    public void an_Apply_taking_a_union_can_be_expanded_to_one_handler_per_case()
    {
        // Discovery: Apply's parameter is a union, so it handles each case type
        var apply = typeof(Account).GetMethod(nameof(Account.Apply))!;
        var unionType = apply.GetParameters()[0].ParameterType;
        var handled = UnionCases(unionType);

        handled.ShouldBe([typeof(Deposited), typeof(Withdrawn), typeof(Frozen)], ignoreOrder: true);

        // Dispatch: the store holds the unwrapped case; wrap it back into the union to call Apply.
        // Activator works because each case has a public constructor taking it.
        var account = new Account();
        object[] stream = [new Deposited(10m), new Withdrawn(3m, "atm"), new Frozen("ops")];
        foreach (var e in stream)
        {
            handled.ShouldContain(e.GetType());
            apply.Invoke(account, [Activator.CreateInstance(unionType, e)]);
        }

        account.Balance.ShouldBe(7m);
        account.IsFrozen.ShouldBeTrue();
    }

    [Fact]
    public void wrapping_can_also_go_through_STJ_UnionConstructor()
    {
        var info = JsonSerializerOptions.Default.GetTypeInfo(typeof(AccountEvent));
        var account = new Account();

        account.Apply((AccountEvent)info.UnionConstructor!(typeof(Deposited), new Deposited(4m)));

        account.Balance.ShouldBe(4m);
    }

    [Fact]
    public void a_case_value_converts_implicitly_to_the_union_so_generated_code_can_pass_it_directly()
    {
        // Generated Apply-calling code (JasperFx codegen) can pass the case where the union is
        // expected: the compiler supplies the conversion.
        var account = new Account();
        Deposited e = new(2m);
        account.Apply(e);
        account.Balance.ShouldBe(2m);
    }
}
