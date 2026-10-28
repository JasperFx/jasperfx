using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #1087: building a missing aggregate for an Apply-only event emitted
/// `new T { Required = default!, ... }`, which overwrote initializers such as
/// `public required List&lt;string&gt; Emails { get; set; } = [];` with null. Marten 8 ran the
/// constructor, so the initializers ran. A catch-all `Create(object)` meant to take control of that
/// case instead made every Apply-fallback arm unreachable (CS8120) inside generated code.
/// </summary>
public class RequiredMemberConstructionTests
{
    private const string Preamble = @"
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public record Created(Guid Id);
public record Touched(Guid Id, string Email);

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}
";

    [Fact]
    public void required_members_are_built_through_the_constructor_so_initializers_run()
    {
        var source = Preamble + @"
public class Doc
{
    public required Guid Id { get; set; }
    public required List<string> Emails { get; set; } = [];
}

public partial class DocProjection : SingleStreamProjection<Doc, Guid>
{
    public Doc Create(Created e) => new() { Id = e.Id, Emails = [] };

    public Doc Apply(Touched e, Doc doc)
    {
        doc.Emails.Add(e.Email);
        return doc;
    }
}
";

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();

        var generated = string.Join("\n", GeneratorHarness.Run(source).generatedSources);
        generated.ShouldNotContain("= default!");
        generated.ShouldContain("(global::Test.Doc)global::System.Activator.CreateInstance(typeof(global::Test.Doc))!");
    }

    [Fact]
    public void a_sets_required_members_constructor_is_called_directly()
    {
        var source = Preamble + @"
public class Doc
{
    [SetsRequiredMembers]
    public Doc() { }

    public required Guid Id { get; set; }
    public required List<string> Emails { get; set; } = [];
}

public partial class DocProjection : SingleStreamProjection<Doc, Guid>
{
    public Doc Create(Created e) => new() { Id = e.Id, Emails = [] };

    public void Apply(Touched e, Doc doc) => doc.Emails.Add(e.Email);
}
";

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();

        var generated = string.Join("\n", GeneratorHarness.Run(source).generatedSources);
        generated.ShouldNotContain("= default!");
        generated.ShouldNotContain("Activator.CreateInstance");
        generated.ShouldContain("new global::Test.Doc()");
    }

    [Fact]
    public void a_catch_all_create_does_not_make_apply_fallback_arms_unreachable()
    {
        var source = Preamble + @"
public class Doc
{
    public Guid Id { get; set; }
    public List<string> Emails { get; set; } = [];
}

public partial class DocProjection : SingleStreamProjection<Doc, Guid>
{
    public Doc Create(Created e) => new() { Id = e.Id };

    // Ignore any other event for a document that does not exist yet
    public Doc? Create(object _) => null;

    public void Apply(Touched e, Doc doc) => doc.Emails.Add(e.Email);
}
";

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void a_create_for_an_interface_owns_the_missing_aggregate_case_for_its_implementations()
    {
        var source = Preamble + @"
public interface IDocEvent { Guid Id { get; } }
public record Renamed(Guid Id, string Name) : IDocEvent;

public class Doc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = """";
}

public partial class DocProjection : SingleStreamProjection<Doc, Guid>
{
    public Doc Create(IDocEvent e) => new() { Id = e.Id };

    public void Apply(Renamed e, Doc doc) => doc.Name = e.Name;
}
";

        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }
}
