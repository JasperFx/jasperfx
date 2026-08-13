using Microsoft.CodeAnalysis;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #653: an evolver whose conventional methods live on the projection instance used to build its own
/// shadow — `new TProjection()`, or GetUninitializedObject when there was no public parameterless
/// constructor — so injected dependencies were null (marten#4787) and constructor state was missing.
/// marten#4787 answered that by injecting the dispatcher into the user's partial class, the
/// member-injection shape #462 had removed, bringing the CS0111 double-load failure back with it.
/// The evolver now takes the projection through its constructor and the runtime passes the
/// registered instance, so dispatch is correct AND the dispatcher stays file-scoped.
/// </summary>
public class EvolverInstanceBindingTests
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

    [Fact]
    public void di_activated_projection_dispatches_through_the_registered_instance()
    {
        var source = Preamble + @"
public partial class DiActivated : SingleStreamProjection<MyAggregate, Guid>
{
    private readonly ISecondaryStore _secondaryStore;
    public DiActivated(ISecondaryStore secondaryStore) { _secondaryStore = secondaryStore; }

    public MyAggregate Create(CreateEvent e) => new MyAggregate();
    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
";
        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = string.Join("\n", generatedSources);

        // No shadow instance of any kind.
        generated.ShouldNotContain("GetUninitializedObject(typeof(global::Test.DiActivated))");
        generated.ShouldNotContain("new global::Test.DiActivated()");

        // The dispatcher stays a file-scoped type, registered for this projection, taking the
        // projection through its constructor.
        generated.ShouldContain("file sealed class DiActivated_GuidEvolver");
        generated.ShouldContain("[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver");
        generated.ShouldContain("public DiActivated_GuidEvolver(global::Test.DiActivated projection)");
        generated.ShouldContain("_projection = projection;");
        generated.ShouldContain("_projection.Create(data)");

        // Nothing is written into the user's class any more.
        generated.ShouldNotContain("partial class DiActivated");

        // No CS7036 "no argument given for required parameter" — the original #4185 hole.
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void di_activated_projection_survives_the_generator_being_loaded_twice()
    {
        // #462: member injection emitted the same override twice and failed with CS0111. A
        // file-scoped evolver is local to its own generated file.
        var source = Preamble + @"
public partial class DiActivatedTwice : SingleStreamProjection<MyAggregate, Guid>
{
    private readonly ISecondaryStore _store;
    public DiActivatedTwice(ISecondaryStore store) { _store = store; }

    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
";
        GeneratorHarness.DoubleLoadGeneratedCodeErrors(source).ShouldBeEmpty();
    }
}
