using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.RuntimeCompiler;
using Shouldly;
using Xunit;

namespace CodegenTests.Bugs;

public class Bug_1086_metadata_references_are_shared
{
    [Fact]
    public void separate_generators_share_the_same_metadata_reference_instances()
    {
        var first = new AssemblyGenerator();
        first.ReferenceAssembly(typeof(Bug_1086_metadata_references_are_shared).Assembly);

        var second = new AssemblyGenerator();
        second.ReferenceAssembly(typeof(Bug_1086_metadata_references_are_shared).Assembly);

        first.References.Count.ShouldBeGreaterThan(2);
        second.References.Count.ShouldBe(first.References.Count);

        // Same instances, not just equal paths: a fresh MetadataReference.CreateFromFile()
        // per compilation is what held ~60-100 MB of native memory until finalization
        for (var i = 0; i < first.References.Count; i++)
        {
            second.References[i].ShouldBeSameAs(first.References[i]);
        }
    }

    [Fact]
    public void a_generator_references_each_path_only_once()
    {
        var generator = new AssemblyGenerator();
        generator.ReferenceAssembly(typeof(Bug_1086_metadata_references_are_shared).Assembly);
        generator.ReferenceAssembly(typeof(GeneratedAssembly).Assembly);

        generator.References.Select(x => x.Display).ShouldBeUnique();
    }

    [Fact]
    public void can_still_compile_repeatedly_with_shared_references()
    {
        for (var i = 0; i < 3; i++)
        {
            var assembly = new GeneratedAssembly(new GenerationRules("Bug1086Generated"));
            var type = assembly.AddType($"Tiny{i}", typeof(Bug_185_empty_frame_list.TestControllerBase));

            new AssemblyGenerator().Compile(assembly);

            type.CompiledType.ShouldNotBeNull();
        }
    }
}
