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

    /// <summary>
    /// The file name of the marker the generator emits into every compilation it is attached to,
    /// candidates or not (jasperfx#887). Filtered out of <see cref="Run" /> so that
    /// <c>generatedSources.ShouldBeEmpty()</c> keeps meaning "nothing was generated for this shape",
    /// which is what every such assertion here is actually about.
    /// </summary>
    public const string MarkerFileName = "JasperFxSourceGeneratorApplied.g.cs";

    /// <summary>Runs the generator and returns its diagnostics plus every generated source.</summary>
    public static (ImmutableArray<Diagnostic> diagnostics, string[] generatedSources) Run(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(source), out _, out var diagnostics);

        var generatedSources = driver.GetRunResult().GeneratedTrees
            .Where(t => !t.FilePath.EndsWith(MarkerFileName, StringComparison.Ordinal))
            .Select(t => t.GetText().ToString())
            .ToArray();

        return (diagnostics, generatedSources);
    }

    /// <summary>One generated file's text, by file name — for the tests that are about the marker itself.</summary>
    public static string GeneratedSource(string source, string fileName)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(source), out _, out _);

        var tree = driver.GetRunResult().GeneratedTrees
            .Single(t => System.IO.Path.GetFileName(t.FilePath) == fileName);

        return tree.GetText().ToString();
    }

    /// <summary>Every generated file path, marker included — for the tests that are about the marker.</summary>
    public static string[] GeneratedFileNames(string source)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(source), out _, out _);

        return driver.GetRunResult().GeneratedTrees
            .Select(t => System.IO.Path.GetFileName(t.FilePath))
            .ToArray();
    }

    /// <summary>
    /// The same, for a compilation that does NOT reference JasperFx.Events — a project carrying the
    /// analyzer with no reference to the library it generates against. The marker must not be emitted
    /// there: the attribute it applies would not resolve, and a marker that breaks a build which
    /// compiles today would be worse than no marker.
    /// </summary>
    public static string[] GeneratedFileNamesWithoutJasperFxReference(string source)
    {
        var references = References()
            .Where(r => (r.Display ?? "").IndexOf("JasperFx.Events", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssemblyWithoutJasperFx",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult().GeneratedTrees
            .Select(t => System.IO.Path.GetFileName(t.FilePath))
            .ToArray();
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
