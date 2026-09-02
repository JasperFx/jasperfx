using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #733: a self-aggregating type whose Create handler is an event-shaped constructor
/// (`public T(Created e)`) rather than a named `static Create` lost that arm entirely once the type
/// also declared ShouldDelete. The Evolve emitters fold <c>info.EventConstructors</c> into their
/// dispatch; DetermineAction built its switch from <c>info.Methods</c> alone, so the Create event
/// had no case at all. Nothing failed loudly — the first Apply-only event that arrived instead
/// built the aggregate through GetUninitializedObject, leaving every constructor-initialized member
/// at its CLR default.
/// </summary>
public class EventConstructorWithShouldDeleteTests
{
    private const string Aggregate = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;

namespace Test;

public sealed record Created(Guid Id, string Name);
public sealed record TagAdded(Guid Id, string Tag);
public sealed record Deleted(Guid Id);

public sealed class TempGuidAggregate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";
    public List<string> Tags { get; set; } = new();

    public TempGuidAggregate(Created @event)
    {
        Id = @event.Id;
        Name = @event.Name;
    }

    public void Apply(TagAdded @event) { Tags.Add(@event.Tag); }

    public bool ShouldDelete(Deleted _) => true;
}
";

    [Fact]
    public void event_constructor_create_is_dispatched_alongside_should_delete()
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Aggregate);
        var generated = generatedSources.Single(s => s.Contains("TempGuidAggregate"));

        generated.ShouldContain("IGeneratedSyncDetermineAction");

        // The Create event needs its own arm invoking the constructor, not a fall-through to
        // the uninitialized-object path.
        generated.ShouldContain("case global::Test.Created data:");
        generated.ShouldContain("snapshot = new global::Test.TempGuidAggregate(data);");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(Aggregate).ShouldBeEmpty();
    }

    [Fact]
    public void event_constructor_type_is_listed_in_event_types()
    {
        var (_, generatedSources) = GeneratorHarness.Run(Aggregate);
        var generated = generatedSources.Single(s => s.Contains("TempGuidAggregate"));

        // EventTypes drives which events the projection is even asked about; omitting the
        // constructor's event type is how a stream that opens with Created gets skipped.
        generated.ShouldContain("typeof(global::Test.Created)");
    }

    /// <summary>
    /// Same shape without ShouldDelete, which routes to EmitSelfAggregatingEvolve. The dispatch
    /// there was already correct; EventTypes was not, so this guards the half of #733 that the
    /// non-delete path shares.
    /// </summary>
    [Fact]
    public void event_constructor_type_is_listed_in_event_types_without_should_delete()
    {
        var source = Aggregate.Replace("    public bool ShouldDelete(Deleted _) => true;", "");

        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = generatedSources.Single(s => s.Contains("TempGuidAggregate"));

        generated.ShouldContain("snapshot = new global::Test.TempGuidAggregate(data);");
        generated.ShouldContain("typeof(global::Test.Created)");
    }
}
