using JasperFx.CodeGeneration;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodegenTests;

// wolverine#4486: `codegen test` (DynamicCodeBuilder.TryBuildAndCompileAll) compiles every ICodeFile
// into its own in-memory assembly (#227). An AotRoots companion that names a SIBLING generated type
// through AttributeArg.TypeNamed -- the whole point of TypeNamed (#743) -- then references a type that
// lives in a different assembly, and the compile fails with CS0234. `codegen write` is unaffected: every
// file lands on disk and the application build compiles them together.
public class AotRootsIsolatedCompilationTests
{
    private const string Namespace = "AotRootsIsolated.Generated";

    [Fact]
    public void codegen_test_compiles_a_file_whose_aot_roots_name_a_type_from_another_file()
    {
        var builder = BuildWith(new SiblingFile(), new RootsFile());

        Should.NotThrow(() =>
            builder.TryBuildAndCompileAll((assembly, services) => new AssemblyGenerator().Compile(assembly, services)));
    }

    [Fact]
    public void codegen_test_keeps_the_roots_that_resolve_inside_the_file_being_compiled()
    {
        var builder = BuildWith(new SiblingFile(), new RootsFile());
        var compiled = new Dictionary<string, string>();

        builder.TryBuildAndCompileAll((assembly, services) =>
        {
            new AssemblyGenerator().Compile(assembly, services, out var code);
            compiled[assembly.GeneratedTypes[0].TypeName] = code;
        });

        var rootsCode = compiled["Registry"];

        // Same file: rooted, and compiled against the real type.
        rootsCode.ShouldContain($"typeof(global::{Namespace}.Registry)");
        // A type that already exists at codegen time is not a sibling: always rooted.
        rootsCode.ShouldContain("typeof(global::CodegenTests.AotRootsIsolatedCompilationTests)");
        // Another file's type: not resolvable in this compilation, so not rooted here.
        rootsCode.ShouldNotContain($"typeof(global::{Namespace}.Sibling)");
    }

    [Fact]
    public void codegen_write_still_roots_every_sibling()
    {
        // The written file is the one ILC sees, and on disk every sibling is compiled together.
        var builder = BuildWith(new SiblingFile(), new RootsFile());

        var code = builder.GenerateAllCode();

        code.ShouldContain($"typeof(global::{Namespace}.Registry)");
        code.ShouldContain($"typeof(global::{Namespace}.Sibling)");
        code.ShouldContain("typeof(global::CodegenTests.AotRootsIsolatedCompilationTests)");
    }

    [Fact]
    public void a_standalone_generated_assembly_keeps_every_named_root()
    {
        // Outside TryBuildAndCompileAll nothing is filtered, whatever the file contains.
        var assembly = GeneratedAssembly.Empty();

        assembly.AddAotRoots("AotRoots", new[] { "Somewhere.Else.Entirely" });

        assembly.GenerateCode().ShouldContain("typeof(global::Somewhere.Else.Entirely)");
    }

    private static DynamicCodeBuilder BuildWith(params ICodeFile[] files)
    {
        return new DynamicCodeBuilder(
            new ServiceCollection().BuildServiceProvider(),
            new ICodeFileCollection[] { new Collection(files) });
    }

    private sealed class SiblingFile : ICodeFile
    {
        public string FileName => "Sibling";

        public void AssembleTypes(GeneratedAssembly assembly)
        {
            assembly.AddType("Sibling", typeof(IWidgetMaker))
                .MethodFor(nameof(IWidgetMaker.Make)).Frames.Code("return 1;");
        }

        public Task<bool> AttachTypes(GenerationRules rules, System.Reflection.Assembly assembly,
            IServiceProvider? services, string containingNamespace) => Task.FromResult(false);

        public bool AttachTypesSynchronously(GenerationRules rules, System.Reflection.Assembly assembly,
            IServiceProvider? services, string containingNamespace) => false;
    }

    // Shaped like Wolverine's HandlerRegistryCodeFile: its own type, then a companion rooting that type
    // and every sibling by name, plus types that already exist.
    private sealed class RootsFile : ICodeFile
    {
        public string FileName => "Registry";

        public void AssembleTypes(GeneratedAssembly assembly)
        {
            assembly.AddType("Registry", typeof(IWidgetMaker))
                .MethodFor(nameof(IWidgetMaker.Make)).Frames.Code("return 2;");

            assembly.AddAotRoots("AotRoots", new[]
            {
                AttributeArg.TypeNamed($"{assembly.Namespace}.Registry"),
                AttributeArg.TypeNamed($"{assembly.Namespace}.Sibling"),
                AttributeArg.Type(typeof(AotRootsIsolatedCompilationTests))
            });
        }

        public Task<bool> AttachTypes(GenerationRules rules, System.Reflection.Assembly assembly,
            IServiceProvider? services, string containingNamespace) => Task.FromResult(false);

        public bool AttachTypesSynchronously(GenerationRules rules, System.Reflection.Assembly assembly,
            IServiceProvider? services, string containingNamespace) => false;
    }

    private sealed class Collection : ICodeFileCollection
    {
        private readonly ICodeFile[] _files;
        public Collection(ICodeFile[] files) { _files = files; }
        public string ChildNamespace => "Generated";
        public GenerationRules Rules { get; } = new("AotRootsIsolated");
        public IReadOnlyList<ICodeFile> BuildFiles() => _files;
    }
}
