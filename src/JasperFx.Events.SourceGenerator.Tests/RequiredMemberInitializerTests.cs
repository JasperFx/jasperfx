using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// When an Apply-only event reaches a projection whose aggregate does not exist yet, the evolver
/// constructs the aggregate itself. For an aggregate with required members that construction was an
/// object initializer, and an object initializer runs after the constructor: `Prop = default!`
/// overwrote `public required List&lt;string&gt; Prop { get; set; } = [];` with null, and the Apply
/// handler that ran next dereferenced it. The reflective `new T()` of earlier versions ran the
/// initializers, so this was a regression for every aggregate that relies on them (#1087).
///
/// #1088 first fixed this by copying self-contained initializers into the object initializer. #1090
/// replaced that with `Activator.CreateInstance(typeof(T))`, which runs the constructor and every
/// initializer, including those that need a using or a member of the aggregate to bind
/// (`Guid.NewGuid()`, a constant). This fixture, with one required member of each shape, is #1088's.
/// The runtime check that each member keeps its declared value is in EventTests,
/// Bug_1087_required_members_on_missing_aggregate.
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
    public void every_required_member_initializer_runs_because_the_aggregate_is_built_through_its_constructor()
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Source);
        diagnostics.ShouldBeEmpty();

        var generated = generatedSources.Single(s => s.Contains("OrderEvolver"));

        generated.ShouldContain("(global::Test.Order)global::System.Activator.CreateInstance(typeof(global::Test.Order))!");

        // No object initializer overwrites what the constructor and initializers set
        generated.ShouldNotContain("new global::Test.Order {");
        generated.ShouldNotContain("= default!");
    }

    [Fact]
    public void the_generated_construction_compiles()
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
