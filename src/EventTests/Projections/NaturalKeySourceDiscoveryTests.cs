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
