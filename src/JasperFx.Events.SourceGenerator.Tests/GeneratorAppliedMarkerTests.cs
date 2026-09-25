using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// jasperfx#887 — the generator emits source only when it finds a candidate, so an assembly it
/// processed and an assembly it never saw were indistinguishable at runtime: both simply have no
/// <c>[GeneratedEvolver]</c>. That ambiguity is what forced the "No source-generated dispatcher
/// found" message to describe a csproj problem and a code-shape problem at once. The marker asserted
/// here is what separates them.
/// </summary>
public class GeneratorAppliedMarkerTests
{
    private const string NothingToGenerate = @"
namespace SomeApp;

// Not an aggregate, not a projection, nothing for the generator to accept.
public class Bystander
{
    public int Count { get; set; }
}
";

    private const string OneSelfAggregatingType = @"
using System;
using JasperFx.Events;

namespace SomeApp;

public record Started(string Name);

public class Thing
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public static Thing Create(Started e) => new() { Name = e.Name };
}
";

    // The whole point: a compilation with zero candidates still carries the marker. Without this,
    // "the analyzer never ran here" and "the analyzer declined your type" look identical.
    [Fact]
    public void the_marker_is_emitted_even_when_there_are_no_candidates()
    {
        GeneratorHarness.GeneratedFileNames(NothingToGenerate)
            .ShouldBe([GeneratorHarness.MarkerFileName]);
    }

    [Fact]
    public void the_marker_is_emitted_alongside_a_generated_dispatcher()
    {
        var files = GeneratorHarness.GeneratedFileNames(OneSelfAggregatingType);

        files.ShouldContain(GeneratorHarness.MarkerFileName);
        files.Length.ShouldBeGreaterThan(1);
    }

    // The analyzer package is analyzer-only and declares no dependency, so a project can carry it
    // without referencing JasperFx.Events at all. Such a project compiles today; an unconditional
    // assembly attribute would turn it into CS0246 for the sake of a diagnostic.
    [Fact]
    public void the_marker_is_skipped_when_jasperfx_events_is_not_referenced()
    {
        GeneratorHarness.GeneratedFileNamesWithoutJasperFxReference(NothingToGenerate)
            .ShouldBeEmpty();
    }

    [Fact]
    public void the_marker_applies_the_attribute_the_runtime_looks_for()
    {
        var marker = GeneratorHarness.GeneratedSource(NothingToGenerate, GeneratorHarness.MarkerFileName);

        // Fully qualified and global::-rooted so no `using` in the consuming assembly can shadow it.
        marker.ShouldContain("[assembly: global::JasperFx.Events.Aggregation.JasperFxSourceGeneratorApplied]");
    }

    // Two copies of the analyzer over one compilation is a real topology (jasperfx#462: the same
    // generator arriving from two referenced packages). Two markers must be legal — hence
    // AllowMultiple on the attribute — because a marker that fails the build it was added to
    // diagnose would be worse than no marker.
    [Fact]
    public void two_copies_of_the_generator_do_not_collide_on_the_marker()
    {
        var errors = GeneratorHarness.DoubleLoadGeneratedCodeErrors(NothingToGenerate);
        errors.ShouldBeEmpty();
    }
}
