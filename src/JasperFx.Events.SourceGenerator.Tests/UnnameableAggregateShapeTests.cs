using Microsoft.CodeAnalysis;
using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #1194: a self-aggregating evolver is a <c>file</c>-scoped type at namespace level, registered by an
/// assembly-level <c>[GeneratedEvolver(typeof(...))]</c>. For an open generic aggregate (CS0246) or one
/// nested behind private / protected (CS0122) the generator emitted that code anyway, breaking the
/// consumer's build inside a file they cannot edit. Present in 3.2.0 as well, so it predates #1191.
///
/// <para>Those declarations now emit nothing and report JFXEVT008, unless another dispatcher in the
/// compilation already covers them: a closed form from a call site, or a partial projection.</para>
/// </summary>
public class UnnameableAggregateShapeTests
{
    private const string UnnameableSelfAggregate = "JFXEVT008";

    private const string Usings = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
";

    private const string Events = @"
public sealed record NameAdded(string Name);
public sealed record Renamed(string Name);
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

    [Fact]
    public void an_open_generic_aggregate_with_apply_methods_is_skipped_with_a_warning()
    {
        var source = Usings + @"
namespace Test;
" + Events + @"
public class Agg<T>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";
    public void Apply(NameAdded e) => Name = e.Name;
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        generatedSources.ShouldBeEmpty();
        var warning = diagnostics.Single(d => d.Id == UnnameableSelfAggregate);
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldContain("'Agg'");
        warning.GetMessage().ShouldContain("it is an open generic type");
        warning.GetMessage().ShouldContain("closed form");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void an_open_generic_aggregate_with_an_evolve_method_is_skipped_with_a_warning()
    {
        var source = Usings + @"
namespace Test;
" + Events + @"
public class Agg<T>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";

    public Agg<T> Evolve(IEvent e)
    {
        if (e.Data is NameAdded added) Name = added.Name;
        return this;
    }
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        generatedSources.ShouldBeEmpty();
        diagnostics.ShouldContain(d => d.Id == UnnameableSelfAggregate);
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("private", "it is declared private")]
    [InlineData("protected", "it is declared protected")]
    [InlineData("private protected", "it is declared private protected")]
    public void an_aggregate_nested_behind_restricted_accessibility_is_skipped_with_a_warning(
        string accessibility, string reason)
    {
        var source = Usings + @"
namespace Test;
" + Events + @"
public class Outer
{
    " + accessibility + @" class Agg
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = """";
        public void Apply(NameAdded e) => Name = e.Name;
    }
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        generatedSources.ShouldBeEmpty();
        var warning = diagnostics.Single(d => d.Id == UnnameableSelfAggregate);
        warning.GetMessage().ShouldContain(reason);
        warning.GetMessage().ShouldContain("internal or public");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_public_aggregate_inside_a_private_class_is_skipped_with_a_warning()
    {
        var source = Usings + @"
namespace Test;
" + Events + @"
public class Host
{
    private class Holder
    {
        public class Agg
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = """";
            public void Apply(NameAdded e) => Name = e.Name;
        }
    }
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        generatedSources.ShouldBeEmpty();
        diagnostics.Single(d => d.Id == UnnameableSelfAggregate).GetMessage()
            .ShouldContain("its containing type 'Holder' is declared private");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void an_aggregate_nested_inside_an_open_generic_type_is_skipped_with_a_warning()
    {
        var source = Usings + @"
namespace Test;
" + Events + @"
public class Outer<T>
{
    public class Agg
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = """";
        public void Apply(NameAdded e) => Name = e.Name;
    }
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        generatedSources.ShouldBeEmpty();
        diagnostics.Single(d => d.Id == UnnameableSelfAggregate).GetMessage()
            .ShouldContain("nested inside the open generic type 'Outer'");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("protected internal")]
    [InlineData("public")]
    public void a_nested_aggregate_reachable_from_the_assembly_still_gets_an_evolver(string accessibility)
    {
        var source = Usings + @"
namespace Test;
" + Events + @"
internal class Outer
{
    " + accessibility + @" class Agg
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = """";
        public void Apply(NameAdded e) => Name = e.Name;
    }
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        diagnostics.ShouldNotContain(d => d.Id == UnnameableSelfAggregate);
        string.Join("\n", generatedSources).ShouldContain(
            "[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.Outer.Agg)");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_closed_form_from_a_call_site_gets_an_evolver_and_quiets_the_warning()
    {
        var source = Usings + SnapshotApi + @"
namespace Test;
" + Events + @"
public class Agg<T>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";
    public void Apply(NameAdded e) => Name = e.Name;
}

public class Registration
{
    public void Register(Marten.Events.Projections.ProjectionOptions opts) => opts.Snapshot<Agg<int>>();
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        diagnostics.ShouldNotContain(d => d.Id == UnnameableSelfAggregate);
        var generated = generatedSources.ShouldHaveSingleItem();
        generated.ShouldContain(
            "[assembly: global::JasperFx.Events.Aggregation.GeneratedEvolver(typeof(global::Test.Agg<int>)");
        generated.ShouldContain("IGeneratedSyncEvolver<global::Test.Agg<int>, global::System.Guid>");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_call_site_closing_the_aggregate_over_a_type_parameter_generates_nothing()
    {
        var source = Usings + SnapshotApi + @"
namespace Test;
" + Events + @"
public class Agg<T>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";
    public void Apply(NameAdded e) => Name = e.Name;
}

public class Registration
{
    public void Register<T>(Marten.Events.Projections.ProjectionOptions opts) => opts.Snapshot<Agg<T>>();
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        generatedSources.ShouldBeEmpty();
        diagnostics.Count(d => d.Id == UnnameableSelfAggregate).ShouldBe(1);
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_call_site_closing_over_a_private_type_generates_nothing()
    {
        var source = Usings + SnapshotApi + @"
namespace Test;
" + Events + @"
public class Agg<T>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";
    public void Apply(NameAdded e) => Name = e.Name;
}

public class Registration
{
    private class Secret { }

    public void Register(Marten.Events.Projections.ProjectionOptions opts) => opts.Snapshot<Agg<Secret>>();
}
";

        var (_, generatedSources) = GeneratorHarness.Run(source);

        generatedSources.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_partial_projection_over_a_closed_form_quiets_the_warning()
    {
        var source = Usings + @"
namespace Test;
" + Events + ProjectionBase + @"
public class Agg<T>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";
    public void Apply(NameAdded e) => Name = e.Name;
}

public partial class AggProjection : SingleStreamProjection<Agg<int>, Guid>
{
    public void Apply(Agg<int> doc, Renamed e) => doc.Name = e.Name;
}
";

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);

        diagnostics.ShouldNotContain(d => d.Id == UnnameableSelfAggregate);
        string.Join("\n", generatedSources).ShouldContain("typeof(global::Test.Agg<int>)");

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_partial_projection_beside_a_private_aggregate_quiets_the_warning()
    {
        var source = Usings + @"
namespace Test;
" + Events + ProjectionBase + @"
public partial class Host
{
    private class Agg
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = """";
        public void Apply(NameAdded e) => Name = e.Name;
    }

    private partial class AggProjection : SingleStreamProjection<Agg, Guid>
    {
        public void Apply(Agg doc, Renamed e) => doc.Name = e.Name;
    }
}
";

        var (diagnostics, _) = GeneratorHarness.Run(source);

        diagnostics.ShouldNotContain(d => d.Id == UnnameableSelfAggregate);
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }
}
