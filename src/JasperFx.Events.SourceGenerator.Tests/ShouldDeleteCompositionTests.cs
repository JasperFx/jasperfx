using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #652: an event type covered by both a delete and a Create/Apply produced two arms in one
/// DetermineAction switch. The delete arm's `case T data:` is unguarded, so the second was
/// unreachable and the consumer build failed with CS8120 inside generated code it cannot edit.
/// A ShouldDelete predicate that returns false still has to fold the event in, so the Create/Apply
/// body belongs in the delete arm's else; a constructor-registered DeleteEvent&lt;T&gt;() is
/// unconditional and suppresses it instead.
/// </summary>
public class ShouldDeleteCompositionTests
{
    private const string Preamble = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

namespace Test;

public class MyEvent { }
public class CreateEvent { }

public class SelfAgg
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public void Apply(MyEvent e) { Count++; }
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, object, object>
    where TDoc : notnull where TId : notnull
{
    protected SingleStreamProjection() : base(AggregationScope.SingleStream) { }
}
";

    [Fact]
    public void should_delete_and_apply_for_the_same_event_type_compiles()
    {
        var source = Preamble + @"
public partial class DeleteAndApply : SingleStreamProjection<SelfAgg, Guid>
{
    public bool ShouldDelete(MyEvent e) => false;
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = SourceFor(generatedSources, "DeleteAndApply");

        // One arm for the event type, with the apply moved into the delete predicate's else.
        CountOccurrences(generated, "case global::Test.MyEvent data:").ShouldBe(1);
        generated.ShouldContain("snapshot.Apply(data);");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void should_delete_predicate_that_returns_false_still_applies_the_event()
    {
        // The semantic half: the self-aggregating path avoided the collision by dropping the Apply
        // arm outright, which made `ShouldDelete(E) => false` silently disable `Apply(E)`.
        var source = @"
using System;
using JasperFx.Events;

namespace Test;

public class MyEvent { }

public class SelfAggWithDelete
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public void Apply(MyEvent e) { Count++; }
    public bool ShouldDelete(MyEvent e) => false;
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = string.Join("\n", generatedSources);

        CountOccurrences(generated, "case global::Test.MyEvent data:").ShouldBe(1);
        generated.ShouldContain("snapshot = null;");
        generated.ShouldContain("else");
        generated.ShouldContain("snapshot.Apply(data);");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void constructor_delete_event_registration_suppresses_apply_for_that_type()
    {
        // DeleteEvent<T>() is unconditional, mirroring the runtime's MatchesAnyDeleteType short
        // circuit, so the Create/Apply arm for that type is dropped rather than moved into an else.
        var source = Preamble + @"
public partial class CtorDeleteAndApply : SingleStreamProjection<SelfAgg, Guid>
{
    public CtorDeleteAndApply()
    {
        DeleteEvent<MyEvent>();
    }

    public SelfAgg Create(CreateEvent e) => new SelfAgg();
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = SourceFor(generatedSources, "CtorDeleteAndApply");

        CountOccurrences(generated, "case global::Test.MyEvent").ShouldBe(1);
        generated.ShouldNotContain("Apply(data)");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void should_delete_and_create_for_the_same_event_type_compiles()
    {
        var source = Preamble + @"
public partial class DeleteAndCreate : SingleStreamProjection<SelfAgg, Guid>
{
    public bool ShouldDelete(CreateEvent e) => false;
    public SelfAgg Create(CreateEvent e) => new SelfAgg();
}
";
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = SourceFor(generatedSources, "DeleteAndCreate");

        CountOccurrences(generated, "case global::Test.CreateEvent data:").ShouldBe(1);
        generated.ShouldContain("if (snapshot == null)");

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    /// <summary>The generated file for one type, when a fixture emits an evolver for several.</summary>
    private static string SourceFor(string[] generatedSources, string typeName)
    {
        return generatedSources.Single(s => s.Contains(typeName));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
