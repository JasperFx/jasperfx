using Microsoft.CodeAnalysis;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// jasperfx#891 — a project can be handed two copies of this analyzer: one bundled inside a store
/// package's own analyzers folder (Marten does this, marten#4557) and one from the standalone
/// JasperFx.Events.SourceGenerator package. Two copies means two instances of the same generator
/// TYPE, and the driver derives a generated file's path from the analyzer assembly name plus the
/// generator type name — so both instances emit identical paths, the `file`-scoped evolvers mangle to
/// the same name, and the build fails with CS0433 naming the same assembly twice.
/// </summary>
/// <remarks>
/// These tests pin the defect rather than a fix, because there is no fix inside the generator: the
/// hint name is its only lever over the path, and making it instance-dependent would be
/// non-deterministic and would defeat incremental caching. The fix is the MSBuild target shipped in
/// JasperFx.Events (buildTransitive/JasperFx.Events.targets), which makes sure the compiler only ever
/// receives one copy. What these assert is the shape of the collision, so that a future change which
/// makes duplicate emission harmless — or which reintroduces a second collision — is visible here.
/// </remarks>
public class DuplicateAnalyzerLoadTests
{
    private const string OneAggregate = @"
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

    /// <summary>
    /// No aggregate, so the generator emits the #887 marker and nothing else. See
    /// <see cref="a_project_with_no_aggregates_builds_clean_under_the_double_load" />.
    /// </summary>
    private const string NoAggregates = @"
namespace SomeApp;

public record Started(string Name);
";

    [Fact]
    public void two_instances_of_the_same_generator_emit_the_same_file_path_twice()
    {
        var paths = GeneratorHarness.GeneratedFileNamesFromTwoInstancesOfTheSameGenerator(OneAggregate);

        // The evolver and the #887 marker, each emitted twice at the same path.
        paths.Length.ShouldBe(4);
        paths.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void the_duplicated_evolver_collides_with_cs0433()
    {
        var errors = GeneratorHarness.ErrorsFromTwoInstancesOfTheSameGenerator(OneAggregate);

        // The fingerprint from marten#5495: one assembly, named twice.
        errors.ShouldContain(x => x.Contains("CS0433") && x.Contains("ThingEvolver"));
    }

    [Fact]
    public void the_duplicated_marker_attribute_does_not_collide()
    {
        // JasperFxSourceGeneratorAppliedAttribute allows multiple applications precisely so that the
        // duplicate-load case costs a second marker rather than CS0579 (jasperfx#887). Asserted here
        // because this is the topology that would otherwise break it.
        var errors = GeneratorHarness.ErrorsFromTwoInstancesOfTheSameGenerator(OneAggregate);

        errors.ShouldNotContain(x => x.Contains("CS0579"));
        errors.ShouldNotContain(x => x.Contains("JasperFxSourceGeneratorApplied"));
    }

    /// <summary>
    /// A project with no aggregates at all builds clean under the double load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fact above is weaker than it looks, and this one is why it needs a companion. Its source
    /// declares an aggregate, so the compilation it inspects is <em>already</em> broken by the CS0433
    /// evolver collision — "no CS0579 among the errors" is being asserted inside a build that failed
    /// for another reason, which would still hold if the marker regressed to single-use.
    /// </para>
    /// <para>
    /// This is the topology where the marker is the ONLY thing emitted twice, because
    /// <c>RegisterPostInitializationOutput</c> fires whether or not the generator found candidates.
    /// So it is the case where dropping <c>AllowMultiple</c> would turn a project that builds clean
    /// today into CS0579 — on a project with no aggregates and no reason to have heard of the
    /// attribute. That is the widened blast radius jasperfx#887 identified, and
    /// <see cref="Shouldly.ShouldBeTestExtensions" />'s empty check is what pins it: not "no CS0579
    /// amongst other errors" but no errors at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void a_project_with_no_aggregates_builds_clean_under_the_double_load()
    {
        GeneratorHarness.ErrorsFromTwoInstancesOfTheSameGenerator(NoAggregates).ShouldBeEmpty();
    }

    [Fact]
    public void one_copy_of_the_generator_is_of_course_fine()
    {
        // The control, so the two tests above cannot pass because of something unrelated to duplication.
        GeneratorHarness.GeneratedCodeErrors(OneAggregate).ShouldBeEmpty();
    }
}
