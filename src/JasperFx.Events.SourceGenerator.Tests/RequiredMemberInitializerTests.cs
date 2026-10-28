using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// When an Apply-only event reaches a projection whose aggregate does not exist yet, the evolver
/// constructs the aggregate itself. For an aggregate with required members that construction is an
/// object initializer, and an object initializer runs after the constructor: `Prop = default!`
/// overwrote `public required List&lt;string&gt; Prop { get; set; } = [];` with null, and the Apply
/// handler that ran next dereferenced it. The reflective `new T()` of earlier versions ran the
/// initializers, so this was a regression for every aggregate that relies on them.
///
/// The emitter now copies a required member's own initializer into the object initializer when the
/// expression is self-contained (needs no using and no member of the aggregate to bind in the
/// generated file), and falls back to `default!` otherwise.
/// </summary>
public class RequiredMemberInitializerTests
{
    private const string Source = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;

namespace Test;

public sealed record OrderPlaced(string Id);
public sealed record LinePlaced(string Id, string Sku);

public sealed class Order
{
    public const int NoPriority = -1;

    public required string Id { get; set; }
    public required string Name { get; set; } = string.Empty;
    public required List<string> Lines { get; set; } = [];
    public required Dictionary<string, string> Tags { get; set; } = new();
    public required HashSet<string> Flags { get; set; } = new() { ""a"", ""b"" };
    public required int Count { get; set; } = 42;
    public required int Limit { get; set; } = int.MaxValue;
    public required int Priority { get; set; } = NoPriority;
    public required string? Note { get; set; } = null;
    public required Guid Token { get; set; } = Guid.NewGuid();
    public required DateTimeOffset Created { get; set; } = global::System.DateTimeOffset.MinValue;
    public required string[] Codes { get; set; } = [""x"", ""y""];
    public required long Total { get; set; } = (long)(10 + 20) * -1;

    public static Order Create(IEvent<OrderPlaced> @event) => new()
    {
        Id = @event.Data.Id, Name = """", Lines = [], Tags = new(), Flags = new(), Count = 0, Limit = 0,
        Priority = 0, Note = null, Token = Guid.Empty, Created = default, Codes = [], Total = 0
    };

    public void Apply(LinePlaced @event) { Lines.Add(@event.Sku); }
}
";

    [Fact]
    public void self_contained_initializers_are_copied_into_the_object_initializer()
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Source);
        diagnostics.ShouldBeEmpty();

        var generated = generatedSources.Single(s => s.Contains("OrderEvolver"));

        generated.ShouldContain("Name = string.Empty");
        generated.ShouldContain("Lines = []");
        generated.ShouldContain("Tags = new()");
        generated.ShouldContain(@"Flags = new() { ""a"", ""b"" }");
        generated.ShouldContain("Count = 42");
        generated.ShouldContain("Limit = int.MaxValue");
        generated.ShouldContain("Note = null");
        generated.ShouldContain("Created = global::System.DateTimeOffset.MinValue");
        generated.ShouldContain(@"Codes = [""x"", ""y""]");
        generated.ShouldContain("Total = (long)(10 + 20) * -1");
    }

    [Fact]
    public void initializers_that_would_not_bind_in_the_generated_file_fall_back_to_default()
    {
        var (_, generatedSources) = GeneratorHarness.Run(Source);
        var generated = generatedSources.Single(s => s.Contains("OrderEvolver"));

        // No initializer at all
        generated.ShouldContain("Id = default!");
        // `Guid` resolves through a using the generated file does not have
        generated.ShouldContain("Token = default!");
        // A constant of the aggregate is not in scope in the evolver
        generated.ShouldContain("Priority = default!");
    }

    [Fact]
    public void the_generated_object_initializer_compiles()
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "RequiredMemberInitializerTests",
            syntaxTrees: [CSharpSyntaxTree.ParseText(Source)],
            references: References(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AggregateEvolverGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        errors.ShouldBeEmpty(string.Join(System.Environment.NewLine, errors.Select(e => e.ToString())));
    }

    private static List<MetadataReference> References()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
        ];
    }
}
