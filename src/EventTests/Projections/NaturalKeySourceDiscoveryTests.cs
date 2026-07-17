using System.Linq;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
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

        var extracted = mapping!.Extractor(new NkCreatedEvent("abc"));
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

        var extracted = mapping!.Extractor(new NkCreatedEvent("self-agg"));
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

        var extracted = mapping!.Extractor(new NkSeparateProjectionCreatedEvent(new NkAggregateKey("separate")));
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
        created!.Extractor(new NkCreatedEvent("first")).ShouldBe(new NkAggregateKey("first"));

        // ...and so does the two-arg evolve method that changes the key.
        var changed = projection.NaturalKeyDefinition.EventMappings
            .SingleOrDefault(m => m.EventType == typeof(NkKeyChangedEvent));
        changed.ShouldNotBeNull();
        changed!.Extractor(new NkKeyChangedEvent("second")).ShouldBe(new NkAggregateKey("second"));
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
