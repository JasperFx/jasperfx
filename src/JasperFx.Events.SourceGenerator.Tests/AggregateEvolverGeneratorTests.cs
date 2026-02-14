using System.Collections.Immutable;
using JasperFx.Events.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

public class AggregateEvolverGeneratorTests
{
    private static (ImmutableArray<Diagnostic> diagnostics, string[] generatedSources) RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Add references for the types the generator needs to resolve
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
        };

        // Also add System.Runtime
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        references.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")));
        references.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Collections.dll")));

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new AggregateEvolverGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();
        var generatedSources = runResult.GeneratedTrees.Select(t => t.GetText().ToString()).ToArray();

        return (diagnostics, generatedSources);
    }

    [Fact]
    public void generates_evolve_override_for_partial_projection_all_sync()
    {
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public class MyAggregate { public int Count { get; set; } }
public class MyEvent { }
public class CreateEvent { }

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

public partial class AllSync : SingleStreamProjection<MyAggregate, Guid>
{
    public MyAggregate Create(CreateEvent e) => new MyAggregate();
    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        generatedSources.Length.ShouldBeGreaterThan(0);
        var generated = generatedSources[0];
        generated.ShouldContain("partial class AllSync");
        generated.ShouldContain("override");
        generated.ShouldContain("Evolve(");
        generated.ShouldContain("Create(data)");
        generated.ShouldContain("Apply(data,");
        generated.ShouldContain("IncludeType<Test.CreateEvent>");
        generated.ShouldContain("IncludeType<Test.MyEvent>");
    }

    [Fact]
    public void generates_evolver_for_self_aggregating_type()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class QuestParty
{
    public Guid Id { get; set; }
    public int Members { get; set; }

    public void Apply(MembersJoined e) { Members += e.Count; }
}

public class MembersJoined { public int Count { get; set; } }
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        generatedSources.Length.ShouldBeGreaterThan(0);
        var generated = generatedSources[0];
        generated.ShouldContain("QuestPartyEvolver");
        generated.ShouldContain("IGeneratedSyncEvolver");
        generated.ShouldContain("[assembly:");
        generated.ShouldContain("snapshot.Apply(data)");
    }

    [Fact]
    public void skips_non_partial_projection()
    {
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public class MyAggregate { public int Count { get; set; } }
public class MyEvent { }

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

public class NonPartial : SingleStreamProjection<MyAggregate, Guid>
{
    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        // Should not generate any override for non-partial class
        generatedSources.ShouldBeEmpty();
        // Should have JFXEVT003 info diagnostic
        diagnostics.Any(d => d.Id == "JFXEVT003").ShouldBeTrue();
    }

    [Fact]
    public void skips_self_aggregating_with_async_methods()
    {
        var source = @"
using System;
using System.Threading.Tasks;
using JasperFx.Events;

namespace Test;

public class AsyncAggregate
{
    public Guid Id { get; set; }
    public async Task Apply(SomeEvent e) { await Task.CompletedTask; }
}

public class SomeEvent { }
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        generatedSources.ShouldBeEmpty();
        diagnostics.Any(d => d.Id == "JFXEVT001").ShouldBeTrue();
    }

    [Fact]
    public void skips_type_without_id_property()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class NoIdAggregate
{
    public int Count { get; set; }
    public void Apply(SomeEvent e) { Count++; }
}

public class SomeEvent { }
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        generatedSources.ShouldBeEmpty();
    }

    [Fact]
    public void generates_determine_action_for_self_aggregating_with_should_delete()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class DeleteableAggregate
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(IncrementEvent e) { Count++; }
    public bool ShouldDelete(DeleteEvent e) => true;
}

public class IncrementEvent { }
public class DeleteEvent { }
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        generatedSources.Length.ShouldBeGreaterThan(0);
        var generated = generatedSources[0];
        generated.ShouldContain("IGeneratedSyncDetermineAction");
        generated.ShouldContain("DetermineAction(");
        generated.ShouldContain("ShouldDelete(data)");
        generated.ShouldContain("snapshot = null;");
    }

    [Fact]
    public void skips_type_with_constructor_event_parameter()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class CtorAggregate
{
    public Guid Id { get; set; }

    public CtorAggregate(CreatedEvent e) { Id = Guid.NewGuid(); }
    public void Apply(UpdatedEvent e) { }
}

public class CreatedEvent { }
public class UpdatedEvent { }
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        // Should not generate because it has a constructor-based creation pattern
        generatedSources.ShouldBeEmpty();
    }
}
