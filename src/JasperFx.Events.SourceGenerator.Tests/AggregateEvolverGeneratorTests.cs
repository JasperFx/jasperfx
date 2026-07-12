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
        var generated = string.Join("\n", generatedSources);

        // The dispatcher is now a standalone, file-scoped evolver registered via an assembly
        // attribute rather than overridden members injected into the projection class. This keeps
        // it safe when the generator loads twice (Marten + Polecat). See #462.
        generated.ShouldContain("file sealed class AllSync_GuidEvolver");
        generated.ShouldContain(
            "[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.MyAggregate), typeof(global::Test.AllSync_GuidEvolver), typeof(global::Test.AllSync))]");
        generated.ShouldContain("global::JasperFx.Events.Aggregation.IGeneratedSyncEvolver<global::Test.MyAggregate, global::System.Guid>");
        generated.ShouldContain("Evolve(");
        generated.ShouldContain("_projection.Create(data)");
        generated.ShouldContain("_projection.Apply(data,");
        generated.ShouldContain("typeof(global::Test.CreateEvent)");
        generated.ShouldContain("typeof(global::Test.MyEvent)");
    }

    [Fact]
    public void di_activated_projection_without_parameterless_ctor_dispatches_through_partial_override()
    {
        // marten#4787: a projection registered via AddProjectionWithServices has only a constructor
        // with injected dependencies (no parameterless ctor). The pre-fix emission built the
        // evolver's private shadow projection via RuntimeHelpers.GetUninitializedObject — which
        // compiled (closing the original #4185 CS7036 hole) but left injected fields null at runtime,
        // NREing the first time a convention method dereferenced one.
        // The fix emits a [GeneratedCode]-attributed override (Evolve / EvolveAsync /
        // DetermineActionAsync) directly into the user's partial class. Dispatch binds to `this`
        // (the DI-built instance), so injected fields are populated. The file-scoped Evolver and the
        // [assembly: GeneratedEvolver(...)] attribute are NOT emitted for this case.
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public class MyAggregate { public int Count { get; set; } }
public class MyEvent { }
public class CreateEvent { }
public interface ISecondaryStore { }

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

public partial class DiActivated : SingleStreamProjection<MyAggregate, Guid>
{
    private readonly ISecondaryStore _secondaryStore;
    public DiActivated(ISecondaryStore secondaryStore) { _secondaryStore = secondaryStore; }

    public MyAggregate Create(CreateEvent e) => new MyAggregate();
    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
";
        var (_, generatedSources) = RunGenerator(source);
        var generated = string.Join("\n", generatedSources);

        // No shadow-instance escape hatch — the override dispatches on `this`.
        generated.ShouldNotContain("GetUninitializedObject(typeof(global::Test.DiActivated))");
        generated.ShouldNotContain("new global::Test.DiActivated()");
        generated.ShouldNotContain("_projection.Create");
        generated.ShouldNotContain("_projection.Apply");

        // No file-scoped Evolver class and no GeneratedEvolver assembly attribute — the runtime
        // selects the partial-class override via isOverridden() before tryUseAssemblyRegisteredEvolver
        // ever runs, so the file-scoped path is unreachable for this case.
        generated.ShouldNotContain("file sealed class");
        generated.ShouldNotContain("[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver");

        // Partial-class override on the user's class, marked [GeneratedCode] so
        // AssembleAndAssertValidity's isSourceGeneratedOverride() accepts it.
        generated.ShouldContain("partial class DiActivated");
        generated.ShouldContain("[global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"JasperFx.Events.SourceGenerator\", \"1.0\")]");
        // Either Evolve (sync, no session/ShouldDelete) or EvolveAsync would do — the projection
        // here is sync with no ShouldDelete, so expect the sync Evolve override.
        generated.ShouldContain("public override global::Test.MyAggregate? Evolve(");

        // The generated override must compile — no CS7036 "no argument for required parameter".
        var diagnostics = CompileWithGenerator(source);
        diagnostics.ShouldNotContain(d => d.Id == "CS7036");
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

        // The partial projection owns the evolver for the aggregate (emitted as a file-scoped type
        // registered against DiagnostiekActiviteit). The Snapshot<T>() call site must NOT additionally
        // emit a redundant self-aggregating evolver for the same aggregate. See #462 / #293.
        allGenerated.ShouldContain(
            "[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.DiagnostiekActiviteit), typeof(global::Test.DiagnostiekActiviteitProjectionEvolver), typeof(global::Test.DiagnostiekActiviteitProjection))]");
        allGenerated.ShouldContain("file sealed class DiagnostiekActiviteitProjectionEvolver");
        allGenerated.ShouldNotContain("DiagnostiekActiviteit_StringEvolver");
    }

    [Fact]
    public void required_member_overridden_from_base_is_not_initialized_twice()
    {
        // Regression: when an aggregate overrides a base `virtual required` property,
        // the inheritance walk in BuildAggregateConstructorExpression collected the
        // member once for the base declaration AND once for the override, emitting
        // `new T { DisplayName = default!, DisplayName = default! }` -> CS1912
        // "Duplicate initialization of member 'DisplayName'".
        var source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Test
{

public class DisplayItemBase
{
    public virtual required string DisplayName { get; set; }
}

public class InheritingDisplayItem : DisplayItemBase
{
    public string Id { get; set; } = """";
    public override required string DisplayName { get; set; }
}

public class DisplayItemCreated
{
    public string Id { get; set; } = """";
    public string DisplayName { get; set; } = """";
}

public class DisplayItemTouched
{
    public string Id { get; set; } = """";
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

public partial class InheritingDisplayItemProjection : SingleStreamProjection<InheritingDisplayItem, string>
{
    public static InheritingDisplayItem Create(DisplayItemCreated e) => new() { Id = e.Id, DisplayName = e.DisplayName };

    // Apply-only event forces the generator's rebuild-from-null snapshot branch,
    // which is where the duplicated initializer was emitted.
    public void Apply(DisplayItemTouched e, InheritingDisplayItem a) { }
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

        allGenerated.ShouldNotContain("DisplayName = default!, DisplayName = default!");
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
        // The ShouldDelete dispatcher is now a file-scoped evolver implementing
        // IGeneratedAsyncDetermineAction rather than an override on the projection. See #462.
        allGenerated.ShouldContain("file sealed class StringQuestProjectionEvolver");
        allGenerated.ShouldContain("global::JasperFx.Events.Aggregation.IGeneratedAsyncDetermineAction<global::Test.StringQuest, string>");
        allGenerated.ShouldContain("public global::System.Threading.Tasks.ValueTask<(global::Test.StringQuest?,");
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
        // The dispatcher for a nested projection is now a file-scoped, top-level evolver whose name
        // encodes the containing-type chain (so two same-named nested projections in sibling parents
        // don't collide), holding the nested projection by its fully-qualified name. No nesting / no
        // injected override means the original CS0115/CS0111 failure modes can't recur. See #462 / #292.
        var (_, generatedSources) = RunGenerator(source);
        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain("file sealed class HostTests_NestedProjectionEvolver");
        allGenerated.ShouldContain("new global::Test.HostTests.NestedProjection()");
        allGenerated.ShouldContain(
            "[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.Counted), typeof(global::Test.HostTests_NestedProjectionEvolver), typeof(global::Test.HostTests.NestedProjection))]");
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

    // marten#4557: a self-aggregating immutable `record` with static Create/Apply must get
    // an evolver emitted from its OWN declaration — with NO Snapshot<T> call site anywhere in
    // the compilation, and WITHOUT being declared `partial`. This is what lets a record
    // aggregate defined in a domain library work when the `Snapshot<T>` registration lives in
    // a different (composition-root) assembly: the `[GeneratedEvolver]` lands in the record's
    // own assembly, which is where the runtime scans. Pre-#4557 the record was skipped here
    // (only Evolve/EvolveAsync triggered records), so it had to be reached via the call site.
    [Fact]
    public void self_aggregating_record_emits_evolver_from_its_own_declaration_without_partial()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public record MyType(Guid Id, string Name, int Count, string LastEvent)
{
    public static MyType Create(MyTypeCreated e) => new(e.Id, e.Name, 0, nameof(MyTypeCreated));
    public static MyType Apply(MyTypeIncremented e, MyType current)
        => current with { Count = current.Count + e.Amount, LastEvent = nameof(MyTypeIncremented) };
}

public record MyTypeCreated(Guid Id, string Name);
public record MyTypeIncremented(int Amount);
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain("MyTypeEvolver");
        allGenerated.ShouldContain("IGeneratedSyncEvolver");
        allGenerated.ShouldContain("[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.MyType)");
        diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error).ShouldBeFalse();
    }

    // jasperfx#432: a self-aggregating `record` (or class) whose Create and Apply methods are split
    // across SEPARATE partial declarations triggered Pipeline 1 once per declaration. Each candidate
    // carries the full (symbol-based) method set, so emitting both produced the same hintName twice and
    // the driver raised CS8785 "the hintName '...Evolver.g.cs' ... must be unique". One evolver per
    // aggregate type must be emitted regardless of how many partial declarations contribute methods.
    [Fact]
    public void self_aggregating_record_split_across_partials_emits_single_evolver()
    {
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public partial record MyEntity(Guid Id, string Value);

public record Creation(string InitialValue);

public record Mutation(string NewValue);

public partial record MyEntity
{
    public static MyEntity Create(IEvent<Creation> evt) => new(evt.StreamId, evt.Data.InitialValue);
}

public partial record MyEntity
{
    public static MyEntity Apply(Mutation evt, MyEntity entity) => entity with { Value = evt.NewValue };
}
";
        var (diagnostics, generatedSources) = RunGenerator(source);

        // No CS8785 (generator-failed) and no duplicate-hintName fallout
        diagnostics.ShouldNotContain(d => d.Id == "CS8785");
        diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error).ShouldBeFalse();

        // Exactly one evolver for MyEntity, and it dispatches BOTH the Create and the Apply events
        var evolvers = generatedSources.Where(s => s.Contains("MyEntityEvolver")).ToArray();
        evolvers.Length.ShouldBe(1);
        evolvers[0].ShouldContain("global::Test.Creation");
        evolvers[0].ShouldContain("global::Test.Mutation");
    }

    // Regression for marten#4755: an [Obsolete] event referenced by the generated evolver must not
    // surface CS0618/CS0612 from the generated tree, or consumers building with TreatWarningsAsErrors
    // break on code they can't edit or suppress.
    [Fact]
    public void generated_evolver_suppresses_obsolete_warnings_for_obsolete_events()
    {
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Test;

[Obsolete(""Use NewEvent instead"")]
public class OldEvent { }
public class NewEvent { }

public class MyAggregate
{
    public Guid Id { get; set; }
    public int Count { get; set; }

#pragma warning disable CS0618
    public void Apply(OldEvent e) { Count++; }
#pragma warning restore CS0618
    public void Apply(NewEvent e) { Count++; }
}
";
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

        // Generated trees are everything in the output compilation except the input source
        var generatedTrees = outputCompilation.SyntaxTrees.Where(t => t != syntaxTree).ToHashSet();
        generatedTrees.ShouldNotBeEmpty();

        // The evolver references OldEvent, so the suppression pragma must be present
        generatedTrees.Any(t => t.GetText().ToString().Contains("#pragma warning disable CS0612, CS0618"))
            .ShouldBeTrue();

        // And no obsolete-usage diagnostic may originate from the generated code
        var obsoleteFromGenerated = outputCompilation.GetDiagnostics()
            .Where(d => d.Id is "CS0618" or "CS0612")
            .Where(d => d.Location.SourceTree != null && generatedTrees.Contains(d.Location.SourceTree!))
            .ToList();

        obsoleteFromGenerated.ShouldBeEmpty();
    }

    // Regression for https://github.com/JasperFx/jasperfx/issues/462 — the generator is bundled as a
    // built-in analyzer inside BOTH Marten and Polecat, so a project referencing both stores loads the
    // SAME incremental generator twice. Each instance emits the same dispatcher against the same source.
    // When the dispatcher was injected as partial members of the user's projection class, the two copies
    // collided with CS0111 (duplicate member); a self-aggregating evolver class collided with CS0101
    // (duplicate type). Emitting the dispatcher as a `file`-scoped evolver makes each copy local to its
    // own generated file, so two instances coexist without colliding. Two generator instances on one
    // driver reproduce the double-load precisely.
    [Fact]
    public void generator_loaded_twice_does_not_produce_duplicate_member_or_type_errors()
    {
        var source = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public class Wallet { public Guid Id { get; set; } public int Balance { get; set; } }
public class Funded { public int Amount { get; set; } }
public class Drained { }

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

// partial-projection path with ShouldDelete (the reported repro shape)
public partial class WalletProjection : SingleStreamProjection<Wallet, Guid>
{
    public Wallet Create(Funded e) => new Wallet { Balance = e.Amount };
    public void Apply(Funded e, Wallet w) { w.Balance += e.Amount; }
    public bool ShouldDelete(Drained e) => true;
}

// self-aggregating path (separate evolver class)
public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public static Tally Create(Funded e) => new Tally();
    public void Apply(Funded e) { Count++; }
}
";

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

        // Two instances of the same generator == the analyzer bundled in two referenced packages.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AggregateEvolverGenerator(), new AggregateEvolverGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var collisionErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id is "CS0111" or "CS0101" or "CS0102")
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToArray();

        collisionErrors.ShouldBeEmpty();
    }

    // Regression for https://github.com/JasperFx/jasperfx/issues/505 — an aggregate with no public
    // parameterless ctor (record with a primary ctor) hits the GetUninitializedObject fallback in
    // BuildAggregateConstructorExpression. The null-snapshot case for an instance Apply that
    // *returns* the aggregate used that cast expression directly as the Apply receiver:
    // `(Cart)RuntimeHelpers.GetUninitializedObject(typeof(Cart)).Apply(data)` — member access binds
    // tighter than the cast, so Apply resolved against `object` and the user's build failed with
    // CS1061. The fix emits the same two-statement local form the async twin uses.
    [Fact]
    public void apply_returning_aggregate_without_parameterless_ctor_compiles_null_snapshot_case()
    {
        var source = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public sealed record CartCreated(string Owner);
public sealed record ItemAdded(string Name);

// Self-aggregating record: primary constructor only (no public parameterless ctor),
// instance Apply method that returns the aggregate.
public sealed record Cart(Guid Id, string Owner, List<string> Items)
{
    public Cart Apply(ItemAdded e) => this with { Items = [.. Items, e.Name] };
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

// Partial projection subclass declaring only Create(IEvent<CartCreated>), so the
// ItemAdded null-snapshot case must construct the aggregate itself.
public partial class CartProjection : SingleStreamProjection<Cart, Guid>
{
    public Cart Create(IEvent<CartCreated> e) => new Cart(e.StreamId, e.Data.Owner, new List<string>());
}
";
        var diagnostics = CompileWithGenerator(source);

        // CS1061 is the exact error shape the issue called out: 'object' does not contain
        // a definition for 'Apply'.
        var castBindingErrors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS1061")
            .Select(d => d.GetMessage())
            .ToArray();

        castBindingErrors.ShouldBeEmpty();

        // Spot-check the emitted form so a future regression that dodges CS1061 some other
        // way still trips: the uninitialized instance lands in a local, and Apply is called
        // on the local — never appended directly to the cast expression.
        var (_, generatedSources) = RunGenerator(source);
        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain(
            "var s = (global::Test.Cart)global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(global::Test.Cart));");
        allGenerated.ShouldContain("return s.Apply(data);");
        allGenerated.ShouldNotContain("GetUninitializedObject(typeof(global::Test.Cart)).Apply(");
    }

    // Same defect through the other sync path (#505): the DI-activated partial-class override
    // added for marten#4787 shares EmitNullSnapshotCases with the file-scoped evolver, so the
    // Evolve override injected into the user's partial class emitted the identical broken cast.
    [Fact]
    public void di_activated_apply_returning_aggregate_without_parameterless_ctor_compiles_null_snapshot_case()
    {
        var source = @"
using System;
using System.Collections.Generic;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public sealed record CartCreated(string Owner);
public sealed record ItemAdded(string Name);
public interface ICartPricing { }

public sealed record Cart(Guid Id, string Owner, List<string> Items)
{
    public Cart Apply(ItemAdded e) => this with { Items = [.. Items, e.Name] };
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}

// DI-only constructor forces the partial-class override path (marten#4787).
public partial class CartProjection : SingleStreamProjection<Cart, Guid>
{
    private readonly ICartPricing _pricing;
    public CartProjection(ICartPricing pricing) { _pricing = pricing; }

    public Cart Create(IEvent<CartCreated> e) => new Cart(e.StreamId, e.Data.Owner, new List<string>());
}
";
        var diagnostics = CompileWithGenerator(source);

        var castBindingErrors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS1061")
            .Select(d => d.GetMessage())
            .ToArray();

        castBindingErrors.ShouldBeEmpty();

        var (_, generatedSources) = RunGenerator(source);
        var allGenerated = string.Join("\n", generatedSources);
        allGenerated.ShouldContain("public override global::Test.Cart? Evolve(");
        allGenerated.ShouldContain(
            "var s = (global::Test.Cart)global::System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(global::Test.Cart));");
        allGenerated.ShouldContain("return s.Apply(data);");
        allGenerated.ShouldNotContain("GetUninitializedObject(typeof(global::Test.Cart)).Apply(");
    }
}
