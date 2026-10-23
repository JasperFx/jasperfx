// jasperfx#777: the AOT roots companion, proven end to end under Native AOT.
//
// Resolves Internal.Generated.Widgets.WidgetMaker exactly as a Critter Stack application resolves its
// pre-generated code: GenerationRules with TypeLoadMode.Static -> StaticTypeLoader -> ICodeFile.
// AttachTypesSynchronously -> Assembly.FindPreGeneratedType. Nothing references WidgetMaker statically,
// so under PublishAot it survives trimming only if the companion in Internal/Generated/AotRoots.cs rooted it.
//
// Exit codes, which build/Build.cs (smoke-test-aot-roots) asserts on:
//   0  found, instantiated, and it answered
//   2  the static-mode lookup did not find the type: what the negative control must produce
//   3  found, but it misbehaved

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.AotRootsSmoke;
using JasperFx.CodeGeneration;

var rules = new GenerationRules("Internal.Generated")
{
    TypeLoadMode = TypeLoadMode.Static,
    ApplicationAssembly = typeof(WidgetCodeFile).Assembly
};

var file = new WidgetCodeFile();

try
{
    file.InitializeSynchronously(rules, new WidgetCodeFiles(rules, file), null);
}
catch (ExpectedTypeMissingException e)
{
    Console.Error.WriteLine($"NOT FOUND: TypeLoadMode.Static could not resolve the pre-generated type. {e.Message}");
    return 2;
}

var made = file.Create().Make();
if (made != 42)
{
    Console.Error.WriteLine($"MISBEHAVED: WidgetMaker.Make() returned {made}, expected 42.");
    return 3;
}

Console.WriteLine($"FOUND: {file.ResolvedType!.FullName} resolved through TypeLoadMode.Static and answered {made}.");
return 0;

namespace JasperFx.AotRootsSmoke
{
    public interface IWidgetMaker
    {
        int Make();
    }

    internal sealed class WidgetCodeFiles(GenerationRules rules, ICodeFile file) : ICodeFileCollection
    {
        public string ChildNamespace => "Widgets";

        public GenerationRules Rules { get; } = rules;

        public IReadOnlyList<ICodeFile> BuildFiles() => [file];
    }

    /// <summary>A code file whose one type was "pre-generated" into Internal/Generated.</summary>
    internal sealed class WidgetCodeFile : ICodeFile
    {
        public string FileName => "WidgetMaker";

        public Type? ResolvedType { get; private set; }

        public void AssembleTypes(GeneratedAssembly assembly)
            => throw new NotSupportedException("Static mode only: this harness never generates code at runtime.");

        public Task<bool> AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider? services,
            string containingNamespace)
            => Task.FromResult(AttachTypesSynchronously(rules, assembly, services, containingNamespace));

        public bool AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider? services,
            string containingNamespace)
        {
            ResolvedType = assembly.FindPreGeneratedType(containingNamespace, FileName);
            return ResolvedType != null;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2072",
            Justification = "The type is kept by the AOT roots companion's [DynamicDependency(All)]; whether that works is what this harness tests.")]
        public IWidgetMaker Create() => (IWidgetMaker)Activator.CreateInstance(ResolvedType!)!;
    }
}
