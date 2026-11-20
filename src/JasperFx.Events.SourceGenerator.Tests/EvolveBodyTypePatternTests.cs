using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// #1243: the evolver's <c>EventTypes</c> filter is built from the event types an Evolve body names.
/// The body was read for declaration patterns only (<c>case Foo f:</c>, <c>case Foo _:</c>,
/// <c>Foo f =&gt;</c>), so a bare type pattern (<c>case Foo:</c>, <c>Foo =&gt;</c>) or a type inside an
/// <c>or</c> pattern was left out of the filter. That event was never fetched and never reached Evolve,
/// with nothing to say so.
/// </summary>
public class EvolveBodyTypePatternTests
{
    private const string Events = @"
using System;
using JasperFx.Events;

namespace Test;

public class Started { }
public class Bare { }
public class Second { }
public class Third { }
public class Guarded { }
public class Excluded { }
";

    private static string EvolverFor(string source, string typeName)
    {
        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();

        return generatedSources.Single(x => x.Contains($"file sealed class {typeName}EvolveEvolver"));
    }

    private static string EventTypesOf(string evolver)
    {
        var start = evolver.IndexOf("EventTypes =>", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1);
        var end = evolver.IndexOf(';', start);
        return evolver.Substring(start, end - start);
    }

    [Fact]
    public void a_bare_case_label_in_a_switch_statement_is_fetched()
    {
        var source = Events + @"
public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Evolve(object e)
    {
        switch (e)
        {
            case Started started:
                Count = 1;
                break;
            case Bare:
                Count++;
                break;
        }
    }
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
    }

    [Fact]
    public void a_bare_case_label_on_the_event_wrapper_is_fetched()
    {
        var source = Events + @"
public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Evolve(IEvent e)
    {
        switch (e)
        {
            case IEvent<Started> started:
                Count = 1;
                break;
            case IEvent<Bare>:
                Count++;
                break;
        }
    }
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
    }

    [Fact]
    public void a_bare_case_label_with_a_when_clause_is_fetched()
    {
        var source = Events + @"
public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Evolve(object e)
    {
        switch (e)
        {
            case Started started:
                Count = 1;
                break;
            case Guarded when Count > 0:
                Count++;
                break;
        }
    }
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Guarded)");
    }

    [Fact]
    public void an_or_pattern_in_a_case_label_is_fetched()
    {
        var source = Events + @"
public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Evolve(object e)
    {
        switch (e)
        {
            case Started started:
                Count = 1;
                break;
            case Bare or Second or (Third):
                Count++;
                break;
        }
    }
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
        eventTypes.ShouldContain("typeof(global::Test.Second)");
        eventTypes.ShouldContain("typeof(global::Test.Third)");
    }

    [Fact]
    public void a_bare_type_pattern_in_a_switch_expression_arm_is_fetched()
    {
        var source = Events + @"
public record Tally(Guid Id, int Count)
{
    public Tally Evolve(object e) => e switch
    {
        Started started => this with { Count = 1 },
        Bare => this with { Count = Count + 1 },
        _ => this
    };
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
    }

    [Fact]
    public void a_declaration_pattern_in_a_switch_expression_arm_is_fetched()
    {
        var source = Events + @"
public record Tally(Guid Id, int Count)
{
    public Tally Evolve(object e) => e switch
    {
        Started started => this with { Count = 1 },
        Bare bare => this with { Count = Count + 1 },
        _ => this
    };
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
    }

    [Fact]
    public void an_or_pattern_in_a_switch_expression_arm_is_fetched()
    {
        var source = Events + @"
public record Tally(Guid Id, int Count)
{
    public Tally Evolve(object e) => e switch
    {
        Started started => this with { Count = 1 },
        Bare or Second => this with { Count = Count + 1 },
        _ => this
    };
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
        eventTypes.ShouldContain("typeof(global::Test.Second)");
    }

    [Fact]
    public void an_or_pattern_in_an_is_expression_is_fetched()
    {
        var source = Events + @"
public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Evolve(object e)
    {
        if (e is Started started) Count = 1;
        if (e is Bare or Second) Count++;
    }
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
        eventTypes.ShouldContain("typeof(global::Test.Second)");
    }

    [Fact]
    public void a_type_under_a_not_pattern_is_not_fetched()
    {
        var source = Events + @"
public class Tally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Evolve(object e)
    {
        switch (e)
        {
            case Started started:
                Count = 1;
                break;
            case Bare and not Excluded:
                Count++;
                break;
        }

        if (e is not Excluded) Count++;
    }
}
";
        var eventTypes = EventTypesOf(EvolverFor(source, "Tally"));

        eventTypes.ShouldContain("typeof(global::Test.Started)");
        eventTypes.ShouldContain("typeof(global::Test.Bare)");
        eventTypes.ShouldNotContain("Excluded");
    }
}
