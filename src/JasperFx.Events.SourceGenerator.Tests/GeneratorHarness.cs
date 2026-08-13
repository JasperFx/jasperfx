using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// Shared compilation harness for the generator's regression suites.
///
/// <para>Each issue's tests live in their own file rather than all landing at the end of
/// AggregateEvolverGeneratorTests, so independent fixes to the generator do not collide on one test
/// file. This type is what makes that practical — the content is deliberately identical wherever it
/// appears, so branches that both introduce it merge without a conflict.</para>
/// </summary>
internal static class GeneratorHarness
{
    private static List<MetadataReference> References()
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
        };

        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        references.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")));
        references.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Collections.dll")));

        return references;
    }

    private static CSharpCompilation Compilation(string source)
    {
        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Runs the generator and returns its diagnostics plus every generated source.</summary>
    public static (ImmutableArray<Diagnostic> diagnostics, string[] generatedSources) Run(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(source), out _, out var diagnostics);

        var generatedSources = driver.GetRunResult().GeneratedTrees
            .Select(t => t.GetText().ToString())
            .ToArray();

        return (diagnostics, generatedSources);
    }

    /// <summary>
    /// Errors reported inside generated files only. The stub projection bases these fixtures use are
    /// not valid types on their own, so an unfiltered assertion would report the harness rather than
    /// the emission under test.
    /// </summary>
    public static string[] GeneratedCodeErrors(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver.RunGeneratorsAndUpdateCompilation(Compilation(source), out var outputCompilation, out _);

        return outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => (d.Location.SourceTree?.FilePath ?? "").Contains("SourceGenerator"))
            .Select(d => d.ToString())
            .ToArray();
    }

    /// <summary>
    /// Compiles with the generator loaded twice, as it is when bundled as a built-in analyzer in two
    /// referenced packages (#462). The second copy is a distinct generator TYPE on purpose: the driver
    /// derives generated file paths from the generator's type name, so two instances of the same type
    /// would collide on path alone (CS0433), which is a different problem entirely.
    /// </summary>
    public static ImmutableArray<Diagnostic> CompileWithTwoCopies(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AggregateEvolverGenerator().AsSourceGenerator(),
            new SecondCopyOfTheGenerator().AsSourceGenerator());

        driver.RunGeneratorsAndUpdateCompilation(Compilation(source), out var outputCompilation, out _);

        return outputCompilation.GetDiagnostics();
    }

    /// <summary>Errors inside generated files after the generator ran twice over one compilation.</summary>
    public static string[] DoubleLoadGeneratedCodeErrors(string source)
    {
        return CompileWithTwoCopies(source)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => (d.Location.SourceTree?.FilePath ?? "").Contains("SourceGenerator"))
            .Select(d => d.ToString())
            .ToArray();
    }
}

/// <summary>
/// Stand-in for the same generator arriving from a second referenced package (#462). Delegates to the
/// real generator; only its type identity differs, which is what gives it its own generated file paths.
/// </summary>
[Generator]
public sealed class SecondCopyOfTheGenerator : IIncrementalGenerator
{
    private readonly AggregateEvolverGenerator _inner = new();

    public void Initialize(IncrementalGeneratorInitializationContext context) => _inner.Initialize(context);
}
