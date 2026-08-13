using Microsoft.CodeAnalysis;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #650: candidacy for an aggregation projection subclass used to be gated on the `partial` modifier,
/// left over from the pre-#462 emission that injected members into the user's class. Since #462 the
/// dispatcher is a standalone file-scoped evolver that needs nothing from the user's declaration, so
/// the gate only produced silence — no evolver, a clean build, and
/// "No source-generated dispatcher found" when the store was built.
///
/// <para>`partial` survives only where the dispatcher is still written into the projection class
/// itself, reported as JFXEVT003.</para>
/// </summary>
public class PartialRequirementTests
{
    private const string Preamble = @"
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
";

    private const string EventProjectionPreamble = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Test;

public interface ITestQuerySession { }

public interface ITestOperations : ITestQuerySession, IStorageOperations
{
    void Store<T>(T entity) where T : notnull;
}

public abstract class TestEventProjection : JasperFxEventProjectionBase<ITestOperations, ITestQuerySession>
{
    protected override void storeEntity<T>(ITestOperations ops, T entity) => ops.Store(entity);
}

public class AuditRecord { public Guid Id { get; set; } }
public class AuditableEvent { }
";

    [Fact]
    public void generates_evolver_for_non_partial_projection()
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class NonPartial : SingleStreamProjection<MyAggregate, Guid>
{
    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
");
        var generated = string.Join("\n", generatedSources);

        generated.ShouldContain("file sealed class NonPartial_GuidEvolver");
        generated.ShouldContain(
            "[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.MyAggregate), typeof(global::Test.NonPartial_GuidEvolver), typeof(global::Test.NonPartial))]");
        generated.ShouldContain("_projection.Apply(data,");

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void generates_evolver_for_non_partial_projection_with_static_methods()
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class NonPartialStatic : SingleStreamProjection<MyAggregate, Guid>
{
    public static MyAggregate Create(CreateEvent e) => new MyAggregate();
    public static void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
");
        var generated = string.Join("\n", generatedSources);

        generated.ShouldContain("global::Test.NonPartialStatic.Create(data)");
        // Static dispatch needs no instance at all, shadow or otherwise.
        generated.ShouldNotContain("_projectionInstance");
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void non_partial_di_activated_projection_dispatches_through_the_registered_instance()
    {
        // #650 alone required `partial` here, because dispatch had to be injected into the user's class
        // to reach a DI-built instance (marten#4787). #653 removed that reason — the evolver takes the
        // projection through its constructor and the runtime passes the registered one — so this shape
        // needs nothing from the declaration either.
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class NonPartialDi : SingleStreamProjection<MyAggregate, Guid>
{
    private readonly ISecondaryStore _store;
    public NonPartialDi(ISecondaryStore store) { _store = store; }

    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
");
        var generated = string.Join("\n", generatedSources);

        generated.ShouldContain("file sealed class NonPartialDi_GuidEvolver");
        generated.ShouldContain("public NonPartialDi_GuidEvolver(global::Test.NonPartialDi projection)");
        generated.ShouldNotContain("GetUninitializedObject(typeof(global::Test.NonPartialDi))");
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void generic_projection_dispatches_through_an_injected_override()
    {
        // A file-scoped evolver cannot name an open generic type, so this used to emit
        // `typeof(global::Test.GenericProjection<T>)` and break the consumer build with CS0246.
        var source = Preamble + @"
public partial class GenericProjection<T> : SingleStreamProjection<MyAggregate, Guid> where T : class
{
    public static void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = string.Join("\n", generatedSources);

        generated.ShouldContain("partial class GenericProjection<T>");
        generated.ShouldNotContain("file sealed class");
        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void nested_non_visible_projection_dispatches_through_an_injected_override()
    {
        // Same story, but CS0122: `file`-scoped types cannot be nested, so the evolver could not
        // reach a projection hidden behind `private`.
        var source = Preamble + @"
public partial class Outer
{
    private partial class Inner : SingleStreamProjection<MyAggregate, Guid>
    {
        public static void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
    }
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = string.Join("\n", generatedSources);

        generated.ShouldContain("partial class Outer");
        generated.ShouldContain("partial class Inner");
        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void reports_error_when_the_containing_type_of_an_injected_projection_is_not_partial()
    {
        // The generated file re-opens every containing type, so a non-partial container would fail
        // the consumer build with CS0260 — name it instead.
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class Outer
{
    private partial class Inner : SingleStreamProjection<MyAggregate, Guid>
    {
        public static void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
    }
}
");

        generatedSources.ShouldBeEmpty();

        var notPartial = diagnostics.Single(d => d.Id == "JFXEVT003");
        notPartial.Severity.ShouldBe(DiagnosticSeverity.Error);
        notPartial.GetMessage().ShouldContain("its containing type 'Outer' is not declared partial");
    }

    [Fact]
    public void non_partial_event_projection_still_reports_an_error()
    {
        // Unlike an aggregation subclass, an EventProjection's dispatcher IS an ApplyAsync override on
        // the user's own class, so `partial` remains genuinely required here.
        var (diagnostics, generatedSources) = GeneratorHarness.Run(EventProjectionPreamble + @"
public class NonPartialEventProjection : TestEventProjection
{
    public AuditRecord Create(AuditableEvent e) => new AuditRecord();
}
");

        generatedSources.ShouldBeEmpty();

        var notPartial = diagnostics.Single(d => d.Id == "JFXEVT003");
        notPartial.Severity.ShouldBe(DiagnosticSeverity.Error);
        notPartial.GetMessage().ShouldContain("EventProjection");
    }

    [Fact]
    public void file_scoped_emission_survives_the_generator_being_loaded_twice()
    {
        // #462: the file-scoped evolver is what makes a twice-loaded generator safe. Widening
        // candidacy to non-partial projections must not walk that back — they take the same path.
        GeneratorHarness.DoubleLoadGeneratedCodeErrors(Preamble + @"
public class NonPartialTwice : SingleStreamProjection<MyAggregate, Guid>
{
    public static void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
").ShouldBeEmpty();
    }
}
