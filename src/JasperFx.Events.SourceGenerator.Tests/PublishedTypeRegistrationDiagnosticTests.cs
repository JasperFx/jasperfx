using Microsoft.CodeAnalysis;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #654: a non-partial EventProjection with an explicit ApplyAsync override was dropped before
/// analysis finished, so the document types it writes were never registered as published types and —
/// alone among the generator's skips — nothing was reported. The registration is a PublishedTypes()
/// override emitted into the user's class, so a non-partial projection genuinely cannot have one;
/// the fix is to say so rather than to emit.
/// </summary>
public class PublishedTypeRegistrationDiagnosticTests
{
    private const string Preamble = @"
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
    public void reports_unregistered_published_types_for_a_non_partial_event_projection()
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class NonPartialExplicitApply : TestEventProjection
{
    public override ValueTask ApplyAsync(ITestOperations operations, IEvent e, CancellationToken cancellation)
    {
        operations.Store(new AuditRecord());
        return default;
    }
}
");

        generatedSources.ShouldBeEmpty();

        var warning = diagnostics.Single(d => d.Id == "JFXEVT006");
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldContain("'AuditRecord'");
        warning.GetMessage().ShouldContain("not declared partial");
    }

    [Fact]
    public void no_diagnostic_when_a_non_partial_event_projection_writes_nothing_registrable()
    {
        // Nothing to register, so nothing to report.
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public class NonPartialNoStores : TestEventProjection
{
    public override ValueTask ApplyAsync(ITestOperations operations, IEvent e, CancellationToken cancellation)
    {
        return default;
    }
}
");

        generatedSources.ShouldBeEmpty();
        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void partial_event_projection_still_registers_its_published_types()
    {
        // The partial path is untouched.
        var (diagnostics, generatedSources) = GeneratorHarness.Run(Preamble + @"
public partial class PartialExplicitApply : TestEventProjection
{
    public override ValueTask ApplyAsync(ITestOperations operations, IEvent e, CancellationToken cancellation)
    {
        operations.Store(new AuditRecord());
        return default;
    }
}
");

        string.Join("\n", generatedSources).ShouldContain("typeof(global::Test.AuditRecord)");
        diagnostics.ShouldNotContain(d => d.Id == "JFXEVT006");
    }
}
