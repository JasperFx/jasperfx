using System.Reflection;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EventTests.EventModeling;

/// <summary>
/// jasperfx#993 — <c>AddDiscoveredEventModels(assembly)</c> registers definitions from the
/// source-generated <c>JasperFx.Generated.DiscoveredEventModels</c> manifest, without a type scan.
/// </summary>
public class DiscoveredEventModelsTests
{
    [Fact]
    public async Task with_a_manifest_registration_enumerates_no_types()
    {
        // GetExportedTypes / GetTypes throw: reaching either fails the test.
        var assembly = new ManifestOnlyAssembly(typeof(FakeManifest));

        using var services = new ServiceCollection()
            .AddDiscoveredEventModels(assembly)
            .BuildServiceProvider();

        var descriptors = await EventModelDiscovery.DiscoverAsync(services);

        descriptors.SelectMany(x => x.Slices).Select(x => x.Name).ShouldBe(["FromManifest"]);
    }

    [Fact]
    public void the_manifest_drops_anything_that_is_not_a_concrete_definition()
    {
        EventModelServiceCollectionExtensions.TryReadDefinitionManifest(
            new ManifestOnlyAssembly(typeof(FakeManifest)), out var types).ShouldBeTrue();

        types.ShouldBe([typeof(ManifestListedModel)]);
    }

    [Fact]
    public void an_empty_manifest_still_counts_as_a_manifest()
    {
        var assembly = new ManifestOnlyAssembly(typeof(EmptyManifest));

        EventModelServiceCollectionExtensions.TryReadDefinitionManifest(assembly, out var types).ShouldBeTrue();
        types.ShouldBeEmpty();

        // And so there is no fallback scan to throw.
        new ServiceCollection().AddDiscoveredEventModels(assembly).ShouldBeEmpty();
    }

    [Fact]
    public async Task without_a_manifest_it_falls_back_to_the_reflective_scan()
    {
        // EventTests is built without JasperFx.SourceGenerator, so it has no manifest.
        EventModelServiceCollectionExtensions.TryReadDefinitionManifest(
            typeof(DiscoveredEventModelsTests).Assembly, out _).ShouldBeFalse();

        using var services = new ServiceCollection()
            .AddSingleton(new EventModelDiscoveryTests.SliceNames("x"))
            .AddDiscoveredEventModels(typeof(DiscoveredEventModelsTests).Assembly)
            .BuildServiceProvider();

        (await EventModelDiscovery.DiscoverAsync(services)).SelectMany(x => x.Slices)
            .ShouldContain(x => x.Origin == new Uri("event-model://ManifestListedModel"));
    }

    public class ManifestListedModel : EventModelDefinition
    {
        public override void Configure(EventModelBuilder model) => model.Command("FromManifest");
    }

    public abstract class AbstractModel : EventModelDefinition;

    /// <summary>The shape JasperFx.SourceGenerator emits.</summary>
    private static class FakeManifest
    {
        public static IReadOnlyList<Type> DefinitionTypes { get; } =
            [typeof(ManifestListedModel), typeof(AbstractModel), typeof(string)];
    }

    private static class EmptyManifest
    {
        public static IReadOnlyList<Type> DefinitionTypes { get; } = [];
    }

    /// <summary>
    /// An assembly that only answers the manifest lookup. Any attempt to enumerate its types throws, which
    /// is how the test proves registration made none.
    /// </summary>
    private sealed class ManifestOnlyAssembly(Type manifest) : Assembly
    {
        public override string FullName => "ManifestOnly, Version=1.0.0.0";

        public override Type? GetType(string name, bool throwOnError, bool ignoreCase)
            => name == "JasperFx.Generated.DiscoveredEventModels" ? manifest : null;

        public override Type[] GetExportedTypes()
            => throw new InvalidOperationException("Registration enumerated the assembly's exported types");

        public override Type[] GetTypes()
            => throw new InvalidOperationException("Registration enumerated the assembly's types");
    }
}
