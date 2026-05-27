using System.Collections.Generic;
using System.IO;
using System.Linq;
using JasperFx;
using JasperFx.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.SourceGenerator.Tests;

// GH-378: the generated input parser cast a Dictionary<string, string?> flag (e.g. the inherited
// NetCoreInput.ConfigFlag) to IDictionary<string, string>, which is CS8619 under
// `#nullable enable` + TreatWarningsAsErrors and broke consumer builds (notably net10 — Wolverine).
public class InputParserGeneratorTests
{
    private const string Source = """
        using System.Collections.Generic;
        using JasperFx.CommandLine;

        namespace App;

        public class SampleInput
        {
            // Mirrors the inherited NetCoreInput.ConfigFlag that broke the Wolverine build.
            public Dictionary<string, string?> ConfigFlag = new();
        }

        public class SampleCommand : JasperFxCommand<SampleInput>
        {
            public override bool Execute(SampleInput input) => true;
        }
        """;

    private static (string? parser, IEnumerable<Diagnostic> diagnostics) Run()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IJasperFxExtension).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
        };

        // Nullable enabled so the CS8619 nullability mismatch would surface if it regressed.
        var compilation = CSharpCompilation.Create("TestAssembly", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new InputParserGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var parser = driver.GetRunResult().GeneratedTrees
            .Select(t => t.GetText().ToString())
            .FirstOrDefault(t => t.Contains("SampleInputParser"));

        return (parser, output.GetDiagnostics());
    }

    [Fact]
    public void dictionary_flag_parser_compiles_clean_under_nullable()
    {
        var (parser, diagnostics) = Run();

        parser.ShouldNotBeNull("The InputParserGenerator should have produced a parser for SampleInput");
        parser.ShouldContain("#nullable disable");
        diagnostics.Where(d => d.Id == "CS8619").ShouldBeEmpty();
    }
}
