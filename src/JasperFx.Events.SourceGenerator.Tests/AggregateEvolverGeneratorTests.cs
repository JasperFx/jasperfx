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

    private static ImmutableArray<Diagnostic> CompileWithGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IEvent).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
        };

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
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        return outputCompilation.GetDiagnostics();
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
        generated.ShouldContain("IncludeType<global::Test.CreateEvent>");
        generated.ShouldContain("IncludeType<global::Test.MyEvent>");
    }

    [Fact]
    public void partial_projection_for_required_member_aggregate_suppresses_snapshot_fallback()
    {
        var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Marten.Events.Projections
{
    public class ProjectionOptions
    {
        public void Snapshot<T>() { }
    }
}

namespace Test
{

public record DiagnostiekActiviteit(string Id)
{
    public required string Aanlevercode { get; set; }
    public required int Prestatiecodelijst { get; set; }

    public DiagnostiekActiviteit(DiagnosticProvidedCareImported e) : this(e.ProvidedCareId)
    {
        Aanlevercode = e.Prestatiecode;
        Prestatiecodelijst = e.Prestatiecodelijst;
    }
}

public class DiagnosticProvidedCareReceived
{
    public string ProvidedCareId { get; set; } = """";
    public string Prestatiecode { get; set; } = """";
    public int Prestatiecodelijst { get; set; }
}

public class DiagnosticProvidedCareImported
{
    public string ProvidedCareId { get; set; } = """";
    public string Prestatiecode { get; set; } = """";
    public int Prestatiecodelijst { get; set; }
}

public class StubOperations : IStorageOperations
{
    public bool EnableSideEffectsOnInlineProjections => false;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(
        string tenantId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IMessageSink> GetOrStartMessageSink() => throw new NotSupportedException();
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, StubOperations, StubOperations>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() { }
}

public partial class DiagnostiekActiviteitProjection : SingleStreamProjection<DiagnostiekActiviteit, string>
{
    public static DiagnostiekActiviteit Create(DiagnosticProvidedCareReceived e) => new(e.ProvidedCareId)
    {
        Aanlevercode = e.Prestatiecode,
        Prestatiecodelijst = e.Prestatiecodelijst
    };

    public void Apply(DiagnosticProvidedCareImported e, DiagnostiekActiviteit a) { }
}

public class Registration
{
    public void Register(Marten.Events.Projections.ProjectionOptions opts)
    {
        opts.Snapshot<DiagnostiekActiviteit>();
    }
}
}
";
        var diagnostics = CompileWithGenerator(source);

        diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.GetMessage())
            .ShouldBeEmpty();

        var (_, generatedSources) = RunGenerator(source);
        var allGenerated = string.Join("\n", generatedSources);

        allGenerated.ShouldContain("partial class DiagnostiekActiviteitProjection");
        allGenerated.ShouldNotContain("[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.DiagnostiekActiviteit)");
        allGenerated.ShouldNotContain("DiagnostiekActiviteit_StringEvolver");
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
    public void emits_async_evolver_for_async_self_aggregating_apply()
    {
        // Async self-aggregating Apply/Create handlers are now supported via
        // IGeneratedAsyncEvolver. The pre-#297 SG would bail with JFXEVT001;
        // we now emit an async EvolveAsync that awaits each handler call.
        // See #297.
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
        var (_, generatedSources) = RunGenerator(source);

        generatedSources.Length.ShouldBeGreaterThan(0);
        var evolver = generatedSources[0];
        evolver.ShouldContain("IGeneratedAsyncEvolver");
        evolver.ShouldContain("public async global::System.Threading.Tasks.ValueTask<global::Test.AsyncAggregate?> EvolveAsync(");
        evolver.ShouldContain("await snapshot.Apply(data)");
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

    // Regression for #324: a pure DCB boundary aggregate has Apply methods but no
    // single-stream identity (no Id, no [AggregateIdentity]). Without opt-in the SG
    // emits nothing (see skips_type_without_id_property above). Marking the type
    // [BoundaryAggregate] opts it into evolver generation, keyed on string TId to
    // match the SingleStreamProjection<T, string> the DCB aggregator builds.
    [Fact]
    public void generates_string_keyed_evolver_for_boundary_aggregate_with_attribute()
    {
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Test;

[BoundaryAggregate]
public class SubscriptionState
{
    public int Count { get; set; }
    public void Apply(Subscribed e) { Count++; }
}

public class Subscribed { }
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        generatedSources.Length.ShouldBeGreaterThan(0);
        var generated = generatedSources[0];
        // The evolver class carries the _String TId-disambiguation suffix
        // (BuildUniqueEvolverName) since the boundary aggregate has no own Id.
        generated.ShouldContain("SubscriptionState_StringEvolver");
        // Identity-less boundary aggregate is keyed on string (decision B in #324).
        generated.ShouldContain("global::JasperFx.Events.Aggregation.IGeneratedSyncEvolver<global::Test.SubscriptionState, string>");
        generated.ShouldContain("[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.SubscriptionState)");
        generated.ShouldContain("snapshot.Apply(data)");
    }

    // Negative half of #324: the marker is required. An identity-less aggregate
    // WITHOUT [BoundaryAggregate] still emits nothing — a bare no-Id aggregate is far
    // more likely a forgot-the-Id mistake than an intentional boundary aggregate, and
    // we don't want to silently swallow that into a string-keyed evolver.
    [Fact]
    public void skips_identity_less_aggregate_without_boundary_attribute()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class SubscriptionState
{
    public int Count { get; set; }
    public void Apply(Subscribed e) { Count++; }
}

public class Subscribed { }
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

    // Regression for https://github.com/JasperFx/jasperfx/issues/288 — when a type at the
    // generator's emission site shadows a parent namespace name, unqualified `Foo.Bar`
    // references resolve to the shadowing type rather than the namespace, producing
    // CS0426: "The type name 'Bar' does not exist in the type 'Foo'". Emitting with the
    // `global::` alias bypasses the shadowing lookup.
    [Fact]
    public void emits_global_prefix_so_namespace_shadowing_types_dont_break_compilation()
    {
        // `EventTests` is both a namespace and a class declared inside it — same shape
        // as the original repro from #287 / #288. We use block-scoped namespaces here so
        // the class `EventTests` and the sibling `EventTests.Projections` namespace are
        // both declared under the root namespace `EventTests`, mirroring the real repro.
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace EventTests
{
    public class EventTests { } // shadows the parent namespace at lookup sites under EventTests.*
}

namespace EventTests.Projections
{
    public class Day { public Guid Id { get; set; } public int Count { get; set; } }
    public class DayEvent { }

    public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
        where TDoc : notnull where TId : notnull
    {
        protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
    }

    public partial class DayProjection : SingleStreamProjection<Day, Guid>
    {
        public void Apply(DayEvent e, Day d) { d.Count++; }
    }
}
";
        // Compile the user source + generator output together and verify no CS0426
        // ("type 'X' does not exist in the type 'Y'") errors leak out — that's the
        // exact compile error the unqualified emit produced before the fix.
        var diagnostics = CompileWithGenerator(source);

        var shadowingErrors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS0426")
            .Select(d => d.GetMessage())
            .ToArray();

        shadowingErrors.ShouldBeEmpty();

        // And spot-check that the generated code actually uses the global:: alias on the
        // user-type references the emit fix targets, so a future regression can't sneak
        // through by accidentally satisfying CS0426 some other way.
        var (_, generatedSources) = RunGenerator(source);
        generatedSources.Length.ShouldBeGreaterThan(0);
        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain("global::EventTests.Projections.Day");
        allGenerated.ShouldContain("global::EventTests.Projections.DayEvent");
    }

    // Regression for the sync `DetermineActionAsync` override emit: when a
    // partial projection has both an `Apply` (sync) and a `ShouldDelete`
    // method, the SG emits a sync method returning `ValueTask<(TDoc?, ActionType)>`
    // but the `snapshot == null` early-return path used to emit a raw tuple
    //
    //   return exists ? (null, ActionType.Delete) : (null, ActionType.Nothing);
    //
    // instead of wrapping it in `new ValueTask<...>(...)`, producing CS8135
    // ("Tuple with 2 elements cannot be converted to type 'ValueTask<...>'")
    // in downstream compilations. The async path was correct (async state
    // machine wraps the tuple) — only the sync branch was broken.
    [Fact]
    public void partial_projection_with_should_delete_compiles_sync_path()
    {
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public class StringQuest
{
    public string Id { get; set; } = """";
    public int Members { get; set; }
}

public class StartQuest { }
public class JoinQuest { }
public class EndQuest { }

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

public partial class StringQuestProjection : SingleStreamProjection<StringQuest, string>
{
    public static StringQuest Create(IEvent<StartQuest> e) => new StringQuest();
    public void Apply(JoinQuest e, StringQuest q) { q.Members++; }
    public bool ShouldDelete(EndQuest e) => true;
}
";
        var diagnostics = CompileWithGenerator(source);

        // Filter to CS8135 (the bug's compile error): "Tuple with N elements
        // cannot be converted to type 'ValueTask<...>'". Stub-setup errors
        // from the minimal test fixture (CS0311 etc. on the stub
        // SingleStreamProjection<,>) are pre-existing and don't relate to the
        // generated code — mirroring the filtering pattern used by the
        // CS0426 regression test for #288.
        var valueTaskTupleErrors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS8135")
            .Select(d => d.GetMessage())
            .ToArray();

        valueTaskTupleErrors.ShouldBeEmpty();

        // Spot-check that the generated sync DetermineActionAsync wraps the
        // snapshot==null tuple in a ValueTask<>, so a future regression that
        // accidentally compiles some other way still trips this assertion.
        var (_, generatedSources) = RunGenerator(source);
        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain("public override global::System.Threading.Tasks.ValueTask<(global::Test.StringQuest?,");
        allGenerated.ShouldContain("new global::System.Threading.Tasks.ValueTask<(global::Test.StringQuest?, global::JasperFx.Events.Daemon.ActionType)>((null, global::JasperFx.Events.Daemon.ActionType.Delete))");
    }

    // Regression for https://github.com/JasperFx/jasperfx/issues/292 — when a partial projection is
    // declared *inside* another type (a common shape in xUnit test files where the projection lives
    // next to the test methods), the SG must walk the ContainingType chain and emit each enclosing
    // type as `partial class` so the generated partial nests correctly. Before the fix the SG emitted
    // its `partial class X` at namespace scope, the two partials referred to different types, and
    // the build broke with CS0115 ("no suitable method found to override").
    [Fact]
    public void partial_projection_nested_inside_parent_class_merges_correctly()
    {
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public class Counted { public Guid Id { get; set; } public int Count { get; set; } }
public class Bumped { }

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

public class HostTests
{
    public partial class NestedProjection : SingleStreamProjection<Counted, Guid>
    {
        public void Apply(Bumped e, Counted c) { c.Count++; }
    }
}
";
        var diagnostics = CompileWithGenerator(source);

        // CS0115 is the exact error shape the issue called out — the generated override
        // didn't find a base method because the partial wasn't nested under HostTests.
        // CS0111 (duplicate member) shows up when sibling-namespace nested projections
        // collide on simple name; same root cause, same regression target.
        var nestingErrors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && (d.Id == "CS0115" || d.Id == "CS0111"))
            .Select(d => d.GetMessage())
            .ToArray();

        nestingErrors.ShouldBeEmpty();

        // Spot-check that the generated output actually wraps with the parent partial,
        // so a future regression can't sneak through by accidentally satisfying CS0115
        // some other way.
        var (_, generatedSources) = RunGenerator(source);
        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain("partial class HostTests");
        allGenerated.ShouldContain("partial class NestedProjection");
    }

    // Regression for https://github.com/JasperFx/jasperfx/issues/293 — when a Marten consumer
    // registers a self-aggregating snapshot via `opts.Projections.Snapshot<TAggregate>(...)`,
    // there is no user projection class for Pipeline 1 to pick up — the aggregate type itself
    // owns the Apply/Create methods. Pipeline 3 detects the call site and runs the analyzer on
    // the aggregate type so the SG emits a self-aggregating evolver for it.
    [Fact]
    public void emits_self_aggregating_evolver_via_snapshot_call_site()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Marten.Events.Projections
{
    // Stub mirroring Marten's surface — namespace prefix `Marten` is what
    // IsKnownSnapshotApi keys on so user-defined Snapshot<T>() helpers in
    // unrelated namespaces don't accidentally trigger Pipeline 3.
    public class ProjectionOptions
    {
        public void Snapshot<T>() { }
    }
}

namespace Test;

public class Counter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public static Counter Create(Started e) => new() { Id = e.Id };
    public void Apply(Bumped _) => Count++;
}

public class Started { public Guid Id { get; set; } }
public class Bumped { }

public class Registration
{
    public void Register(Marten.Events.Projections.ProjectionOptions opts)
    {
        opts.Snapshot<Counter>();
    }
}
";
        var (_, generatedSources) = RunGenerator(source);

        generatedSources.Length.ShouldBeGreaterThan(0);
        var allGenerated = string.Join("\n", generatedSources);

        // The evolver class plus its [GeneratedEvolver] assembly attribute are what the
        // runtime's tryUseAssemblyRegisteredEvolver scan picks up on `typeof(TDoc).Assembly`.
        allGenerated.ShouldContain("CounterEvolver");
        allGenerated.ShouldContain("[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.Counter)");
    }

    [Fact]
    public void nullable_aggregate_parameter_attribute_does_not_crash_hint_name_generation()
    {
        var source = @"
#nullable enable
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Test;

public class ReadAggregateAttribute(string memberName) : Attribute, IRefersToAggregate;

public record EigenPrestatie
{
    public required string Id { get; init; }
    public required string Prestatiecode { get; init; }

    public static EigenPrestatie Create(EigenPrestatieAangemaakt e) => new()
    {
        Id = e.PrestatieId,
        Prestatiecode = e.Prestatiecode
    };
}

public class EigenPrestatieAangemaakt
{
    public string PrestatieId { get; set; } = """";
    public string Prestatiecode { get; set; } = """";
}

public class EigenTariefAanmaken
{
    public string PrestatieId { get; set; } = """";
}

public static class EigenTariefAanmakenEndpoint
{
    public static void Validate(
        EigenTariefAanmaken request,
        [ReadAggregate(nameof(EigenTariefAanmaken.PrestatieId))]
        EigenPrestatie? prestatie)
    {
    }
}
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        diagnostics
            .Where(d => d.Id == "CS8785")
            .Select(d => d.GetMessage())
            .ShouldBeEmpty();

        generatedSources.Length.ShouldBeGreaterThan(0);
        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain("IGeneratedSyncEvolver<global::Test.EigenPrestatie, string>");
        allGenerated.ShouldContain("EigenPrestatieEvolver");
    }

    // Negative case for #293: a same-named Snapshot<T>() helper in a user namespace must NOT
    // trip Pipeline 3, otherwise unrelated user code with a generic Snapshot<T> method would
    // start producing spurious evolvers for arbitrary type arguments.
    [Fact]
    public void does_not_match_snapshot_call_in_unrelated_namespace()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace User.Helpers
{
    public class Helper
    {
        public void Snapshot<T>() { }
    }
}

namespace Test;

public class NotAnAggregate
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public static NotAnAggregate Create(Started e) => new() { Id = e.Id };
    public void Apply(Bumped _) => Count++;
}

public class Started { public Guid Id { get; set; } }
public class Bumped { }

public class Registration
{
    public void Register(User.Helpers.Helper helper)
    {
        helper.Snapshot<NotAnAggregate>();
    }
}
";
        var (_, generatedSources) = RunGenerator(source);

        // Pipeline 1 picks up NotAnAggregate (it has conventional methods on it), so the SG
        // does emit a self-aggregating evolver — that's expected and orthogonal to Pipeline 3.
        // What we're guarding here is that the unrelated User.Helpers.Snapshot<T>() call site
        // doesn't *trigger* Pipeline 3 to push another candidate through (which dedup would
        // suppress anyway, but if Pipeline 3 had matched, a Snapshot<T> targeting a type with
        // no methods would still try to run the analyzer). The functional signal we can assert:
        // no extra generated sources beyond the one Pipeline 1 produces.
        generatedSources.Length.ShouldBe(1);
    }

    // Regression for https://github.com/JasperFx/jasperfx/issues/295 — DiscoverMethodsOnType
    // and InferIdentityType used to enumerate direct members only, so an aggregate that
    // inherited Apply/Create/Id from a user base class was invisible to the SG. The
    // analyzer now walks the inheritance chain (stopping at framework bases) so an evolver
    // is still emitted.
    [Fact]
    public void discovers_inherited_apply_create_and_id_on_self_aggregating_type()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class Bumped { }
public class Started { public Guid Id { get; set; } }

public abstract class CounterBase
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(Bumped _) { Count++; }
}

public class Counter : CounterBase
{
    public static Counter Create(Started e) => new() { Id = e.Id };
}
";
        var (_, generatedSources) = RunGenerator(source);

        // The aggregate is `Counter`; Apply + Id come from CounterBase. Before #295 the SG
        // saw no methods and no Id directly on Counter and skipped it entirely. After the
        // inheritance walk the evolver is emitted, keyed on Counter, with both Apply
        // (from the base) and Create (from the derived) dispatched.
        generatedSources.Length.ShouldBeGreaterThan(0);
        var counterEvolver = generatedSources.FirstOrDefault(s => s.Contains("CounterEvolver"));
        counterEvolver.ShouldNotBeNull();

        counterEvolver!.ShouldContain("[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.Counter)");

        // Both event types should appear in the dispatch:
        counterEvolver.ShouldContain("global::Test.Bumped");
        counterEvolver.ShouldContain("global::Test.Started");
    }

    // Verifies the inheritance walk respects the override/new resolution rule: a derived
    // declaration with the same signature wins over the base declaration, matching how the
    // language itself dispatches. Without the dedupe, both would land in the result list
    // and the emitter would generate duplicate case branches.
    [Fact]
    public void inherited_apply_is_shadowed_by_derived_override_with_same_signature()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class Bumped { }

public class BaseCounter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public virtual void Apply(Bumped _) { Count++; }
}

public class DerivedCounter : BaseCounter
{
    public override void Apply(Bumped _) { Count += 10; }
}
";
        var diagnostics = CompileWithGenerator(source);

        // Duplicate `case Test.Bumped:` branches would surface as CS0152 (the switch already
        // contains a case for that label) on the generated code.
        var dupCaseErrors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS0152")
            .Select(d => d.GetMessage())
            .ToArray();

        dupCaseErrors.ShouldBeEmpty();
    }

    [Fact]
    public void event_constructor_becomes_implicit_create()
    {
        // Bucket 1 in JasperFx#297-followup: a public single-argument
        // constructor whose parameter is a user-defined event type acts as an
        // implicit Create handler. The pre-#276 reflection runtime did the
        // same; we keep the behavior in the SG-emitted code.
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
        var (_, generatedSources) = RunGenerator(source);

        generatedSources.Length.ShouldBeGreaterThan(0);
        var evolver = generatedSources[0];
        evolver.ShouldContain("snapshot = new global::Test.CtorAggregate(data);");
        evolver.ShouldContain("snapshot.Apply(data)");
        evolver.ShouldContain("case global::Test.CreatedEvent");
        evolver.ShouldContain("case global::Test.UpdatedEvent");
    }
}
