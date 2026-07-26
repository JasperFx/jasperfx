using System.Linq;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class NaturalKeySourceDiscoveryTests
{
    [Fact]
    public void discovers_natural_key_source_on_instance_method_of_aggregate()
    {
        // Classic pattern: [NaturalKeySource] on an instance Apply method of a mutable
        // aggregate. This pathway already worked — keep it covered.
        var projection = new NkInstanceMethodProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        var mapping = projection.NaturalKeyDefinition!.EventMappings
            .SingleOrDefault(m => m.EventType == typeof(NkCreatedEvent));
        mapping.ShouldNotBeNull();

        var extracted = mapping!.Extractor(new Event<NkCreatedEvent>(new NkCreatedEvent("abc")));
        extracted.ShouldBe(new NkAggregateKey("abc"));
    }

    [Fact]
    public void discovers_natural_key_source_on_static_factory_of_self_aggregating_aggregate()
    {
        // Regression for https://github.com/JasperFx/marten/issues/4277: the discovery
        // scan previously skipped static methods on docType when the projection and
        // aggregate were the same (self-aggregating records/classes). A static factory
        // such as `public static TDoc Create(TEvent e) => new(...);` is the canonical
        // self-aggregating shape and MUST be picked up.
        var projection = new NkSelfAggregatingProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        var mapping = projection.NaturalKeyDefinition!.EventMappings
            .SingleOrDefault(m => m.EventType == typeof(NkCreatedEvent));
        mapping.ShouldNotBeNull();

        var extracted = mapping!.Extractor(new Event<NkCreatedEvent>(new NkCreatedEvent("self-agg")));
        extracted.ShouldBe(new NkSelfAggregate(default, new NkAggregateKey("self-agg")).Key);
    }

    [Fact]
    public void discovers_natural_key_source_on_static_method_of_separate_projection_class()
    {
        // Sibling coverage: a separate projection class with a static [NaturalKeySource]
        // continues to work via the property-matching fallback on the event payload.
        var projection = new NkSeparateProjectionClass();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        var mapping = projection.NaturalKeyDefinition!.EventMappings
            .SingleOrDefault(m => m.EventType == typeof(NkSeparateProjectionCreatedEvent));
        mapping.ShouldNotBeNull();

        var extracted = mapping!.Extractor(
            new Event<NkSeparateProjectionCreatedEvent>(
                new NkSeparateProjectionCreatedEvent(new NkAggregateKey("separate"))));
        extracted.ShouldBe(new NkAggregateKey("separate"));
    }

    [Fact]
    public void discovers_natural_key_source_on_static_evolve_method_that_changes_the_key()
    {
        // Regression for https://github.com/JasperFx/marten/issues/4966: a static
        // [NaturalKeySource] method that EVOLVES the aggregate and changes the natural key —
        //   public static TDoc Apply(TEvent e, TDoc current) => current with { Key = ... };
        // — must also produce an event mapping. Previously buildExtractor only knew how to call
        // a one-arg static factory, so the two-arg evolve method threw while building the call
        // and was silently skipped, leaving the mt_natural_key table stale after the key changed
        // (never inserting the new key on live append OR rebuild).
        var projection = new NkEvolvingKeyProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();

        // The create factory still maps.
        var created = projection.NaturalKeyDefinition!.EventMappings
            .SingleOrDefault(m => m.EventType == typeof(NkCreatedEvent));
        created.ShouldNotBeNull();
        created!.Extractor(new Event<NkCreatedEvent>(new NkCreatedEvent("first")))
            .ShouldBe(new NkAggregateKey("first"));

        // ...and so does the two-arg evolve method that changes the key.
        var changed = projection.NaturalKeyDefinition.EventMappings
            .SingleOrDefault(m => m.EventType == typeof(NkKeyChangedEvent));
        changed.ShouldNotBeNull();
        changed!.Extractor(new Event<NkKeyChangedEvent>(new NkKeyChangedEvent("second")))
            .ShouldBe(new NkAggregateKey("second"));
    }

    // ───────────────── jasperfx#569 / marten#5041 ─────────────────

    [Fact]
    public void ievent_handlers_produce_a_mapping()
    {
        // Bug 1. IEvent<T> is a first class signature everywhere else in aggregation discovery, but
        // buildExtractor explicitly gave up on it: the extractor only received the event DATA, so
        // there was no IEvent to hand the method. Control fell through to the property-matching
        // fallback, which cannot match a strong-typed key carried on the event as a string, and the
        // method was dropped with no error, no log, and no configuration-time validation. The
        // downstream symptom was a natural key lookup table that was simply never written for that
        // event type — FetchLatest returning null after a rename, live and on rebuild alike.
        var projection = new NkIEventProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        projection.NaturalKeyDefinition!.DiscoveryProblems.ShouldBeEmpty();

        var mapping = projection.NaturalKeyDefinition.EventMappings
            .SingleOrDefault(m => m.EventType == typeof(NkCodeChangedViaEvent));
        mapping.ShouldNotBeNull();

        mapping!.Extractor(new Event<NkCodeChangedViaEvent>(new NkCodeChangedViaEvent("B")))
            .ShouldBe(new NkCode("B"));
    }

    [Fact]
    public void a_key_extraction_method_needs_no_aggregate_at_all()
    {
        // The dedicated signature: a static [NaturalKeySource] returning the natural key type is a
        // pure function of the event, so it binds even for an aggregate that CANNOT be fabricated
        // (this one declares a required member). That is the supported answer to Bug 2 — nothing
        // constructs a blank aggregate and nothing runs the user's aggregation code.
        var projection = new NkKeyFactoryProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        projection.NaturalKeyDefinition!.DiscoveryProblems.ShouldBeEmpty();

        projection.NaturalKeyDefinition.EventMappings
            .Single(m => m.EventType == typeof(NkRegistered))
            .Extractor(new Event<NkRegistered>(new NkRegistered("A")))
            .ShouldBe(new NkCode("A"));

        // ...and the same signature taking IEvent<T>, so a key derived from event metadata rather
        // than the body alone is expressible now that the extractor receives the whole event.
        projection.NaturalKeyDefinition.EventMappings
            .Single(m => m.EventType == typeof(NkCodeChangedViaEvent))
            .Extractor(new Event<NkCodeChangedViaEvent>(new NkCodeChangedViaEvent("B")) { StreamKey = "s-1" })
            .ShouldBe(new NkCode("s-1/B"));
    }

    [Fact]
    public void will_not_invoke_a_handler_against_an_aggregate_it_cannot_safely_build()
    {
        // Bug 2. The fabricated-aggregate path emitted Expression.New(docType) and invoked the
        // user's Apply against it — and Expression.New bypasses C# required-member enforcement, so
        // History was null on an aggregate the user could never have constructed that way. The
        // ArgumentNullException propagated out of the extractor, out of the inline natural key
        // maintenance, and aborted a plain SaveChangesAsync. It is now refused up front.
        var projection = new NkRequiredMembersProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        projection.NaturalKeyDefinition!.EventMappings.ShouldBeEmpty();

        var problem = projection.NaturalKeyDefinition.DiscoveryProblems.ShouldHaveSingleItem();
        problem.Method.Name.ShouldBe(nameof(NkRequiredMembersAggregate.Apply));
        problem.EventType.ShouldBe(typeof(NkCodeChangedInline));
        problem.Reason.ShouldContain("required members");
    }

    [Fact]
    public void an_unbindable_source_method_fails_at_configuration_time()
    {
        // Bug 3/4. Discovery used to `catch { }` and move on, so the user annotated their methods,
        // got no warning of any kind, and found out at runtime. Name the method and the reason
        // while the projection is being registered instead.
        var ex = Should.Throw<InvalidProjectionException>(
            () => new NkRequiredMembersProjection().AssembleAndAssertValidity());

        ex.Message.ShouldContain(nameof(NkRequiredMembersAggregate.Apply));
        ex.Message.ShouldContain("required members");
        ex.Message.ShouldContain("NaturalKeyFor");
    }

    [Fact]
    public void an_explicit_registration_wins_and_clears_the_discovery_problem()
    {
        // Bug 3. NaturalKeyBuilder.SetBy was exactly the escape hatch a user needed when discovery
        // could not bind their method — and its constructor was internal, with nothing in
        // JasperFx.Events or Marten ever constructing it. It is reachable now, and an explicit
        // registration both overrides discovery and satisfies validation.
        var projection = new NkExplicitlyConfiguredProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        projection.NaturalKeyDefinition!.DiscoveryProblems.ShouldBeEmpty();

        projection.NaturalKeyDefinition.EventMappings
            .Single(m => m.EventType == typeof(NkCodeChangedInline))
            .Extractor(new Event<NkCodeChangedInline>(new NkCodeChangedInline("C")))
            .ShouldBe(new NkCode("C"));

        Should.NotThrow(() => projection.AssembleAndAssertValidity());
    }

    [Fact]
    public void an_explicit_registration_replaces_a_discovered_mapping_for_the_same_event()
    {
        var projection = new NkEvolvingKeyProjection();
        projection.NaturalKeyFor(x => x.SetBy<NkCreatedEvent>(e => new NkAggregateKey("overridden:" + e.Key)));

        projection.NaturalKeyDefinition!.EventMappings
            .Count(m => m.EventType == typeof(NkCreatedEvent)).ShouldBe(1);

        projection.NaturalKeyDefinition.EventMappings
            .Single(m => m.EventType == typeof(NkCreatedEvent))
            .Extractor(new Event<NkCreatedEvent>(new NkCreatedEvent("first")))
            .ShouldBe(new NkAggregateKey("overridden:first"));
    }

    [Fact]
    public void an_event_carrying_two_candidate_keys_is_not_resolved_by_declaration_order()
    {
        // Reading a matching property off the event body is preferred over invoking user code, but
        // "the first property of the key's type" is a guess. When an event carries both the old and
        // the new key, defer to the method that actually knows which is which.
        var projection = new NkSwapProjection();

        projection.NaturalKeyDefinition.ShouldNotBeNull();
        projection.NaturalKeyDefinition!.DiscoveryProblems.ShouldBeEmpty();

        projection.NaturalKeyDefinition.EventMappings
            .Single(m => m.EventType == typeof(NkCodeSwapped))
            .Extractor(new Event<NkCodeSwapped>(new NkCodeSwapped(new NkCode("old"), new NkCode("new"))))
            .ShouldBe(new NkCode("new"));
    }

    [Fact]
    public void a_source_method_with_no_event_parameter_is_reported()
    {
        var projection = new NkNoEventParameterProjection();

        var problem = projection.NaturalKeyDefinition!.DiscoveryProblems.ShouldHaveSingleItem();
        problem.Method.Name.ShouldBe(nameof(NkNoEventParameterAggregate.Whoops));
        problem.Reason.ShouldContain("no event parameter");
    }
}

// ───────────────────────── fixtures ─────────────────────────

public record NkAggregateKey(string Value);

public record NkCreatedEvent(string Key);

// Classic instance-method aggregate (pre-#4277 behavior).
public class NkInstanceAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkAggregateKey Key { get; set; } = default!;

    [NaturalKeySource]
    public void Apply(NkCreatedEvent e) => Key = new NkAggregateKey(e.Key);
}

public class NkInstanceMethodProjection : SingleStreamProjection<NkInstanceAggregate, NkAggregateKey>
{
}

// Self-aggregating record with a static [NaturalKeySource] factory — the #4277 shape.
public sealed record NkSelfAggregate(Guid Id, [property: NaturalKey] NkAggregateKey Key)
{
    [NaturalKeySource]
    public static NkSelfAggregate Create(NkCreatedEvent e) => new(Guid.NewGuid(), new NkAggregateKey(e.Key));
}

public class NkSelfAggregatingProjection : SingleStreamProjection<NkSelfAggregate, NkAggregateKey>
{
}

// Separate projection class with a static method — the property-matching fallback path.
public class NkSeparateProjectionAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkAggregateKey Key { get; set; } = default!;
}

public record NkSeparateProjectionCreatedEvent(NkAggregateKey Key);

public class NkSeparateProjectionClass : SingleStreamProjection<NkSeparateProjectionAggregate, NkAggregateKey>
{
    [NaturalKeySource]
    public static NkSeparateProjectionAggregate Create(NkSeparateProjectionCreatedEvent e)
        => new() { Key = e.Key };
}

// #4966 fixture: a self-aggregating record (settable-property record → has a public
// parameterless ctor) whose natural key is set by a create factory and later CHANGED by a
// two-arg static evolve method. This is the shape from the reported repro.
public record NkKeyChangedEvent(string NewKey);

public sealed record NkEvolvingKeyAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkAggregateKey Key { get; set; } = default!;

    [NaturalKeySource]
    public static NkEvolvingKeyAggregate Create(NkCreatedEvent e)
        => new() { Key = new NkAggregateKey(e.Key) };

    [NaturalKeySource]
    public static NkEvolvingKeyAggregate Apply(NkKeyChangedEvent e, NkEvolvingKeyAggregate current)
        => current with { Key = new NkAggregateKey(e.NewKey) };
}

public class NkEvolvingKeyProjection : SingleStreamProjection<NkEvolvingKeyAggregate, NkAggregateKey>
{
}

// ───────── jasperfx#569 fixtures — the marten#5041 repro shapes ─────────

public sealed record NkCode(string Value);

public record NkRegistered(string Code);

public record NkCodeChangedViaEvent(string NewCode);

public record NkCodeChangedInline(string NewCode);

public sealed record NkIEventAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkCode Code { get; set; } = default!;

    [NaturalKeySource]
    public static NkIEventAggregate Create(NkRegistered e) => new() { Code = new NkCode(e.Code) };

    [NaturalKeySource]
    public static NkIEventAggregate Apply(IEvent<NkCodeChangedViaEvent> e, NkIEventAggregate current)
        => current with { Code = new NkCode(e.Data.NewCode) };
}

public class NkIEventProjection : SingleStreamProjection<NkIEventAggregate, Guid>
{
}

// An aggregate that CANNOT be fabricated — it declares a required member, so Expression.New would
// hand the user's method an aggregate C# itself would never have let them create.
public sealed record NkKeyFactoryAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkCode Code { get; set; } = default!;

    public required IEnumerable<NkCode> History { get; set; }

    [NaturalKeySource]
    public static NkCode KeyFor(NkRegistered e) => new(e.Code);

    [NaturalKeySource]
    public static NkCode KeyFor(IEvent<NkCodeChangedViaEvent> e) => new($"{e.StreamKey}/{e.Data.NewCode}");
}

public class NkKeyFactoryProjection : SingleStreamProjection<NkKeyFactoryAggregate, Guid>
{
}

// The reported repro: an instance Apply on a required-member aggregate whose body touches state
// other than the key. Fabricating a blank aggregate here threw ArgumentNullException out of a
// plain event append.
public sealed record NkRequiredMembersAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkCode Code { get; set; } = default!;

    public required IEnumerable<NkCode> History { get; set; }

    [NaturalKeySource]
    public void Apply(NkCodeChangedInline e)
    {
        Code = new NkCode(e.NewCode);
        History = History.Append(Code);
    }
}

public class NkRequiredMembersProjection : SingleStreamProjection<NkRequiredMembersAggregate, Guid>
{
}

// Same unbindable shape, but the projection registers the mapping explicitly. The [NaturalKeySource]
// method is deliberately not named Apply/Create so that the aggregation side of validation is
// unambiguous and this test is only about the natural key.
public sealed record NkExplicitlyConfiguredAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkCode Code { get; set; } = default!;

    public required IEnumerable<NkCode> History { get; set; }

    [NaturalKeySource]
    public void RenameTo(NkCodeChangedInline e)
    {
        Code = new NkCode(e.NewCode);
        History = History.Append(Code);
    }
}

public class NkExplicitlyConfiguredProjection : SingleStreamProjection<NkExplicitlyConfiguredAggregate, Guid>
{
    public NkExplicitlyConfiguredProjection()
    {
        NaturalKeyFor(x => x.SetBy<NkCodeChangedInline>(e => new NkCode(e.NewCode)));
    }

    public override NkExplicitlyConfiguredAggregate? Evolve(NkExplicitlyConfiguredAggregate? snapshot, Guid id,
        IEvent e) => snapshot;
}

// An event carrying both the old and the new key — "first property of the right type" would answer
// with the old one.
public record NkCodeSwapped(NkCode Previous, NkCode Next);

public sealed record NkSwapAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkCode Code { get; set; } = default!;

    [NaturalKeySource]
    public static NkSwapAggregate Apply(NkCodeSwapped e, NkSwapAggregate current)
        => current with { Code = e.Next };
}

public class NkSwapProjection : SingleStreamProjection<NkSwapAggregate, Guid>
{
}

public sealed record NkNoEventParameterAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkCode Code { get; set; } = default!;

    [NaturalKeySource]
    public void Whoops()
    {
    }
}

public class NkNoEventParameterProjection : SingleStreamProjection<NkNoEventParameterAggregate, Guid>
{
}
