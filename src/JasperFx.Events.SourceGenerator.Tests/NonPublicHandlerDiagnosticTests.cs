using Microsoft.CodeAnalysis;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #1196: generated dispatch calls only public Apply / Create / ShouldDelete methods and constructors.
/// A non-public one used to be skipped without a word, leaving a projection that compiles, runs and
/// never applies that event. Under reflection-based dispatch the same code worked, so this is exactly
/// what an upgrade produces. Each such member is now reported as JFXEVT009.
/// </summary>
public class NonPublicHandlerDiagnosticTests
{
    private const string NonPublicHandler = "JFXEVT009";

    private const string Usings = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
";

    private const string Events = @"
public sealed record OrderPlaced(string Customer);
public sealed record OrderShipped(DateTimeOffset At);
public sealed record OrderCancelled;
";

    private const string ProjectionBase = @"
public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}
";

    private const string SnapshotApi = @"
namespace Marten.Events.Projections
{
    public class ProjectionOptions
    {
        public void Snapshot<T>() { }
    }
}
";

    private static string Source(string body, string preamble = "") =>
        Usings + preamble + "\nnamespace Test;\n" + Events + body;

    private static Diagnostic[] Warnings(string source)
    {
        var (diagnostics, _) = GeneratorHarness.Run(source);
        return diagnostics.Where(d => d.Id == NonPublicHandler).ToArray();
    }

    [Fact]
    public void a_private_apply_beside_public_ones_on_a_self_aggregate_is_reported()
    {
        var source = Source(@"
public class OrderSummary
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = """";
    public bool Shipped { get; set; }

    public void Apply(OrderPlaced e) => Customer = e.Customer;
    private void Apply(OrderShipped e) => Shipped = true;
}
");

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        var warning = diagnostics.Single(d => d.Id == NonPublicHandler);
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldBe(
            "'OrderSummary.Apply(OrderShipped)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.");

        // Reported on the method itself, not the type.
        var span = warning.Location.SourceSpan;
        source.Substring(span.Start, span.Length).ShouldBe("Apply");
        source.Substring(0, span.Start).ShouldEndWith("private void ");

        // The public handler is still dispatched as before.
        generatedSources.ShouldNotBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("private")]
    [InlineData("protected")]
    [InlineData("internal")]
    [InlineData("protected internal")]
    [InlineData("private protected")]
    public void every_non_public_accessibility_is_reported(string accessibility)
    {
        var warnings = Warnings(Source(@"
public class OrderSummary
{
    public Guid Id { get; set; }
    public bool Shipped { get; set; }

    public void Apply(OrderPlaced e) { }
    " + accessibility + @" void Apply(OrderShipped e) => Shipped = true;
}
"));

        warnings.Length.ShouldBe(1);
        warnings[0].GetMessage().ShouldContain("'OrderSummary.Apply(OrderShipped)'");
    }

    [Fact]
    public void non_public_create_and_should_delete_are_reported()
    {
        var warnings = Warnings(Source(@"
public class OrderSummary
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = """";

    public void Apply(OrderShipped e) { }
    private static OrderSummary Create(OrderPlaced e) => new() { Customer = e.Customer };
    internal bool ShouldDelete(OrderCancelled e) => true;
}
"));

        warnings.Select(w => w.GetMessage()).ShouldBe(new[]
        {
            "'OrderSummary.Create(OrderPlaced)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
            "'OrderSummary.ShouldDelete(OrderCancelled)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
        }, ignoreOrder: true);
    }

    [Fact]
    public void handler_shapes_with_session_and_ievent_parameters_are_reported()
    {
        var warnings = Warnings(Source(@"
public interface IQuerySession { }

public class OrderSummary
{
    public Guid Id { get; set; }
    public bool Shipped { get; set; }

    public void Apply(OrderPlaced e) { }
    private Task Apply(IEvent<OrderShipped> e, IQuerySession session, CancellationToken token) => Task.CompletedTask;
}
"));

        warnings.Length.ShouldBe(1);
        warnings[0].GetMessage().ShouldContain("Apply(IEvent<OrderShipped>, IQuerySession, CancellationToken)");
    }

    [Fact]
    public void a_non_public_method_that_is_not_a_handler_shape_is_not_reported()
    {
        // An Apply with an extra argument the dispatcher has no slot for, and a ShouldDelete that does
        // not return bool, would be passed over even if public, so their accessibility is not the
        // reason they are not called.
        Warnings(Source(@"
public class OrderSummary
{
    public Guid Id { get; set; }

    public void Apply(OrderPlaced e) { }
    private void Apply(OrderShipped e, int retries) { }
    private void ShouldDelete(OrderCancelled e) { }
    private void Apply() { }
}
")).ShouldBeEmpty();
    }

    [Fact]
    public void a_non_public_method_marked_jasperfx_ignore_is_not_reported()
    {
        Warnings(Source(@"
public class OrderSummary
{
    public Guid Id { get; set; }

    public void Apply(OrderPlaced e) { }

    [JasperFxIgnore]
    private void Apply(OrderShipped e) { }
}
")).ShouldBeEmpty();
    }

    [Fact]
    public void a_type_whose_only_handlers_are_non_public_and_nothing_names_it_is_not_reported()
    {
        // Nothing says this is an aggregate: a private Apply called from the type's own public methods
        // is an ordinary pattern outside event sourcing. The call-site test below is the other half.
        var source = Source(@"
public class Order
{
    public Guid Id { get; set; }
    public bool Shipped { get; private set; }

    public void Ship() => Apply(new OrderShipped(DateTimeOffset.UtcNow));
    private void Apply(OrderShipped e) => Shipped = true;
}
");

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        diagnostics.ShouldNotContain(d => d.Id == NonPublicHandler);
        generatedSources.ShouldBeEmpty();
    }

    [Fact]
    public void a_type_whose_only_handlers_are_non_public_is_reported_when_a_snapshot_call_names_it()
    {
        var source = Source(@"
public class Order
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = """";

    private void Apply(OrderPlaced e) => Customer = e.Customer;
    private void Apply(OrderShipped e) { }
}

public static class Registration
{
    public static void Configure(Marten.Events.Projections.ProjectionOptions opts) => opts.Snapshot<Order>();
}
", SnapshotApi);

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        diagnostics.Where(d => d.Id == NonPublicHandler).Select(d => d.GetMessage())
            .ShouldBe(new[]
            {
                "'Order.Apply(OrderPlaced)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
                "'Order.Apply(OrderShipped)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
            }, ignoreOrder: true);

        // Still nothing to dispatch, so still nothing emitted.
        generatedSources.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("partial ")]
    public void a_projection_whose_only_handlers_are_non_public_is_reported(string partial)
    {
        var source = Source(ProjectionBase + @"
public class OrderSummary
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = """";
}

public " + partial + @"class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    private void Apply(OrderSummary view, OrderPlaced e) => view.Customer = e.Customer;
    protected OrderSummary Create(OrderShipped e) => new();
}
");

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        diagnostics.Where(d => d.Id == NonPublicHandler).Select(d => d.GetMessage()).ShouldBe(new[]
        {
            "'OrderSummaryProjection.Apply(OrderSummary, OrderPlaced)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
            "'OrderSummaryProjection.Create(OrderShipped)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
        }, ignoreOrder: true);

        generatedSources.ShouldBeEmpty();
    }

    [Fact]
    public void a_non_public_apply_on_the_aggregate_of_a_projection_is_reported_once()
    {
        // The aggregate is reached twice: by the projection's analysis and by its own declaration
        // (it has a public handler and an Id, so it is a self-aggregate too). One warning.
        var warnings = Warnings(Source(ProjectionBase + @"
public class OrderSummary
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = """";

    public void Apply(OrderShipped e) { }
    private void Apply(OrderPlaced e) => Customer = e.Customer;
}

public class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    public void Apply(OrderSummary view, OrderCancelled e) { }
}
"));

        warnings.Length.ShouldBe(1);
        warnings[0].GetMessage().ShouldContain("'OrderSummary.Apply(OrderPlaced)'");
    }

    [Fact]
    public void a_projection_with_its_own_dispatch_override_is_not_reported()
    {
        // Conventional methods are not dispatched at all once Evolve is overridden, public or not.
        Warnings(Source(ProjectionBase + @"
public class OrderSummary
{
    public Guid Id { get; set; }
}

public class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    public override OrderSummary? Evolve(OrderSummary? snapshot, Guid id, IEvent e) => snapshot;

    private void Apply(OrderSummary view, OrderPlaced e) { }
}
")).ShouldBeEmpty();
    }

    [Fact]
    public void a_self_aggregate_with_an_evolve_method_is_not_reported()
    {
        Warnings(Source(@"
public class OrderSummary
{
    public Guid Id { get; set; }

    public OrderSummary Evolve(IEvent e) => this;

    private void Apply(OrderPlaced e) { }
}
")).ShouldBeEmpty();
    }

    [Fact]
    public void a_handler_split_across_partial_declarations_is_reported_once()
    {
        var warnings = Warnings(Source(@"
public partial class OrderSummary
{
    public Guid Id { get; set; }
    public void Apply(OrderPlaced e) { }
}

public partial class OrderSummary
{
    public void Apply(OrderCancelled e) { }
    private void Apply(OrderShipped e) { }
}
"));

        warnings.Length.ShouldBe(1);
    }

    [Fact]
    public void a_non_public_event_constructor_is_reported()
    {
        var source = Source(@"
public class OrderSummary
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = """";

    public OrderSummary() { }
    private OrderSummary(OrderPlaced e) => Customer = e.Customer;

    public void Apply(OrderShipped e) { }
}
");

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        var warning = diagnostics.Single(d => d.Id == NonPublicHandler);
        warning.GetMessage().ShouldBe(
            "'OrderSummary.OrderSummary(OrderPlaced)' looks like a projection handler but is not public, so it will not be called. Make it public if it should create the aggregate from that event.");

        generatedSources.ShouldNotBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void constructors_that_are_not_event_constructors_are_not_reported()
    {
        // The parameterless one is reached through [UnsafeAccessor] (#1188). One taking the identity
        // type sets a strong-typed id, one taking the aggregate copies it, and one taking a framework
        // or simple type is never an event constructor, public or not.
        Warnings(Source(@"
public sealed record OrderId(Guid Value);

public class OrderSummary
{
    public OrderId Id { get; set; } = new(Guid.Empty);

    private OrderSummary() { }
    private OrderSummary(OrderId id) => Id = id;
    protected OrderSummary(OrderSummary other) => Id = other.Id;
    private OrderSummary(string name) { }
    private OrderSummary(int count) { }

    public void Apply(OrderShipped e) { }
}

public record OrderView(Guid Id)
{
    public void Apply(OrderShipped e) { }
}
")).ShouldBeEmpty();
    }

    [Fact]
    public void a_boundary_aggregate_with_only_non_public_handlers_reports_each_one_beside_jfxevt007()
    {
        var (diagnostics, _) = GeneratorHarness.Run(Source(@"
[BoundaryAggregate]
public class OrderBoundary
{
    private void Apply(OrderPlaced e) { }
}
"));

        diagnostics.ShouldContain(d => d.Id == "JFXEVT007");
        diagnostics.Single(d => d.Id == NonPublicHandler).GetMessage()
            .ShouldContain("'OrderBoundary.Apply(OrderPlaced)'");
    }

    private const string EventProjectionPreamble = @"
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
";

    [Fact]
    public void non_public_project_create_and_transform_on_an_event_projection_are_reported()
    {
        var warnings = Warnings(Source(EventProjectionPreamble + @"
public partial class AuditProjection : TestEventProjection
{
    public void Project(OrderCancelled e, ITestOperations ops) { }
    private void Project(OrderPlaced e, ITestOperations ops) { }
    internal AuditRecord Create(OrderShipped e) => new();
    protected AuditRecord Transform(IEvent<OrderCancelled> e) => new();
}
"));

        warnings.Select(w => w.GetMessage()).ShouldBe(new[]
        {
            "'AuditProjection.Project(OrderPlaced, ITestOperations)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
            "'AuditProjection.Create(OrderShipped)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
            "'AuditProjection.Transform(IEvent<OrderCancelled>)' looks like a projection handler but is not public, so it will not be called. Make it public, or rename it if it is not a handler.",
        }, ignoreOrder: true);
    }

    [Fact]
    public void an_event_projection_whose_only_handlers_are_non_public_is_reported()
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Source(EventProjectionPreamble + @"
public partial class AuditProjection : TestEventProjection
{
    private void Project(OrderPlaced e, ITestOperations ops) { }
}
"));

        diagnostics.Single(d => d.Id == NonPublicHandler).GetMessage()
            .ShouldContain("'AuditProjection.Project(OrderPlaced, ITestOperations)'");
        generatedSources.ShouldBeEmpty();
    }
}
