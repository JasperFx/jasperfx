using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// Overriding Evolve / EvolveAsync / DetermineAction / DetermineActionAsync is the documented way to
/// opt out of conventional dispatch, and it needs nothing from the generator. AnalyzeProjectionSubclass
/// returns null for those before candidacy is considered at all — which matters now that JFXEVT003 is
/// an error, since a false positive here would fail the build rather than print a stray message.
/// </summary>
public class OwnDispatchOverrideTests
{
    private const string Preamble = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Test;

public class MyAggregate { public int Count { get; set; } }
public class MyEvent { }
public interface ISecondaryStore { }

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}
";

    [Theory]
    [InlineData("public override global::Test.MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e) => snapshot;")]
    [InlineData("public override ValueTask<MyAggregate?> EvolveAsync(MyAggregate? snapshot, Guid id, object session, IEvent e, CancellationToken cancellation) => new(snapshot);")]
    public void a_non_partial_projection_that_overrides_evolve_is_left_alone(string overrideDeclaration)
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class WritesItsOwnDispatch : SingleStreamProjection<MyAggregate, Guid>
{
    " + overrideDeclaration + @"
}
");

        generatedSources.ShouldBeEmpty();
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void a_non_partial_projection_that_overrides_evolve_and_has_conventional_methods_is_left_alone()
    {
        // The two together are a configuration conflict the runtime rejects at registration
        // (AssembleAndAssertValidity), not something to fail the build over.
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class OverrideAndConventions : SingleStreamProjection<MyAggregate, Guid>
{
    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e) => snapshot;

    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
");

        generatedSources.ShouldBeEmpty();
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void a_di_activated_projection_that_overrides_evolve_is_left_alone()
    {
        // The shape that would otherwise take the JFXEVT003 branch — instance conventional methods,
        // no public parameterless constructor, not partial — but it dispatches itself.
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class DiActivatedWithOwnDispatch : SingleStreamProjection<MyAggregate, Guid>
{
    private readonly ISecondaryStore _store;
    public DiActivatedWithOwnDispatch(ISecondaryStore store) { _store = store; }

    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e) => snapshot;

    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
");

        generatedSources.ShouldBeEmpty();
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void a_non_partial_event_projection_that_overrides_apply_async_is_left_alone()
    {
        // Same principle on the EventProjection side: an explicit ApplyAsync IS the dispatcher.
        var (diagnostics, _) = GeneratorHarness.Run(@"
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

public class EventProjectionWithOwnDispatch : TestEventProjection
{
    public override ValueTask ApplyAsync(ITestOperations operations, IEvent e, CancellationToken cancellation)
    {
        return default;
    }
}
");

        diagnostics.ShouldNotContain(d => d.Id == "JFXEVT003");
    }
}
