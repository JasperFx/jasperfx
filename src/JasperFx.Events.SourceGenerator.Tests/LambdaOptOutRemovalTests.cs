using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #655: the analyzer used to skip any projection whose constructor invoked ProjectEvent /
/// CreateEvent / DeleteEvent with arguments, and its EventProjection counterpart matched the raw
/// substring "Project&lt;" over the constructor's text. Those inline lambda registration APIs were
/// removed in JasperFx 2.0 / Marten 9.0 (#286), so the only thing either check could still match was
/// user code that happens to look like them — silently leaving that projection with no dispatcher,
/// surfacing later as "No source-generated dispatcher found" at store construction.
/// </summary>
public class LambdaOptOutRemovalTests
{
    [Fact]
    public void a_constructor_call_named_like_a_removed_lambda_api_no_longer_suppresses_dispatch()
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

public partial class ProjectionWithOwnHelper : SingleStreamProjection<MyAggregate, Guid>
{
    public ProjectionWithOwnHelper()
    {
        ProjectEvent(""not the removed API, just a method of mine"");
    }

    private void ProjectEvent(string note) { }

    public void Apply(MyEvent e, MyAggregate agg) { agg.Count++; }
}
";
        var (_, generatedSources) = GeneratorHarness.Run(source);

        string.Join("\n", generatedSources).ShouldContain("ProjectionWithOwnHelper");
    }

    [Fact]
    public void an_event_projection_constructor_mentioning_project_no_longer_suppresses_dispatch()
    {
        // The EventProjection half was a substring match, so even a comment could disable generation.
        var source = @"
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

public partial class EventProjectionWithComment : TestEventProjection
{
    public EventProjectionWithComment()
    {
        // Project<AuditRecord> used to live here
    }

    public AuditRecord Create(AuditableEvent e) => new AuditRecord();
}
";
        var (_, generatedSources) = GeneratorHarness.Run(source);

        string.Join("\n", generatedSources).ShouldContain("EventProjectionWithComment");
    }
}
