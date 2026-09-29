using Shouldly;

namespace JasperFx.Events.SourceGenerator.Tests;

/// <summary>
/// marten#3942: an aggregate whose only single-argument public constructor takes its identity or
/// another value (`public record Widget(string Id)`) must not have that constructor treated as an
/// event-shaped Create. The runtime path already refuses it (CreateMethodCollection: `!IsSimple()`,
/// jasperfx ee88feb). The source generator did not, because DiscoverEventConstructors relied on a
/// display-string prefix test, and `string`, `int` and other keyword types render as the C# keyword,
/// not as `System.*`; enums are not in System at all. The result was `typeof(string)` in the
/// evolver's EventTypes, which ProjectionGraph.Add / Describe and the daemon's event-type filter
/// hand to Marten's event graph, where `string` is refused ("This type cannot be used as a Marten
/// document").
///
/// The generator is deliberately stricter than the runtime's `!IsSimple()`: it also keeps object,
/// decimal, nullables, arrays and tuples out, since a constructor taking one of those is not a
/// Create handler and would only widen the event filter.
/// </summary>
public class StringIdentityConstructorTests
{
    private const string Template = @"
using System;
using JasperFx.Events;

namespace Test;

public sealed record WidgetCreated;
public sealed record WidgetRenamed(string Name);
public enum WidgetStatus { Active, Retired }

public sealed class Widget
{
    public string Id { get; set; } = """";
    public string Name { get; set; } = """";

    public Widget(__CTOR_PARAMETER__ value) { }

    public static Widget Create(IEvent<WidgetCreated> @event) => new(default!);

    public void Apply(WidgetRenamed @event) { Name = @event.Name; }
}
";

    [Theory]
    [InlineData("string", "typeof(string)")]
    [InlineData("int", "typeof(int)")]
    [InlineData("bool", "typeof(bool)")]
    [InlineData("decimal", "typeof(decimal)")]
    [InlineData("object", "typeof(object)")]
    [InlineData("WidgetStatus", "typeof(global::Test.WidgetStatus)")]
    [InlineData("int?", "typeof(int?)")]
    [InlineData("WidgetStatus?", "typeof(global::Test.WidgetStatus?)")]
    [InlineData("string[]", "typeof(string[])")]
    [InlineData("(int, string)", "typeof((int, string))")]
    public void a_value_constructor_parameter_is_not_an_event_type(string constructorParameter, string unwanted)
    {
        var source = Template.Replace("__CTOR_PARAMETER__", constructorParameter);

        var (diagnostics, generatedSources) = GeneratorHarness.Run(source);
        var generated = generatedSources.Single(s => s.Contains("Widget"));

        // The real events are still listed...
        generated.ShouldContain("typeof(global::Test.WidgetCreated)");
        generated.ShouldContain("typeof(global::Test.WidgetRenamed)");

        // ...and the constructor's value parameter is not one of them.
        generated.ShouldNotContain(unwanted);

        diagnostics.ShouldBeEmpty();
        GeneratorHarness.GeneratedCodeErrors(source).ShouldBeEmpty();
    }

    [Fact]
    public void an_event_shaped_constructor_is_still_an_event_type()
    {
        var source = Template.Replace("__CTOR_PARAMETER__", "WidgetRenamed");

        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = generatedSources.Single(s => s.Contains("Widget"));

        generated.ShouldContain("typeof(global::Test.WidgetRenamed)");
    }

    // The same trap sat behind the event types the generator reads out of an Evolve body: a
    // `case WidgetStatus status:` arm resolved to a type that was not in System, so it was listed in
    // EventTypes next to the real events.
    private const string EvolveTemplate = @"
using System;
using JasperFx.Events;
using JasperFx.Events.Aggregation;

namespace Test;

public sealed record WidgetCreated;
public enum WidgetStatus { Active, Retired }

[BoundaryAggregate]
public class WidgetState
{
    public int Count { get; set; }

    public void Evolve(IEvent e)
    {
        switch (e.Data)
        {
            case WidgetCreated created: Count++; break;
            case __PATTERN_TYPE__ value: Count--; break;
        }
    }
}
";

    // Only an enum is exercised here. The generator resolves an Evolve-body type name by looking it up
    // among the types declared in the compilation's own assembly (FindTypeByName), so keyword types
    // such as `string` and `int` never resolve and never reached EventTypes; an enum declared next to
    // the aggregate does resolve, and used to be listed.
    [Fact]
    public void an_enum_pattern_in_an_evolve_body_is_not_an_event_type()
    {
        var source = EvolveTemplate.Replace("__PATTERN_TYPE__", "WidgetStatus");

        var (_, generatedSources) = GeneratorHarness.Run(source);
        var generated = generatedSources.Single(s => s.Contains("WidgetState"));

        generated.ShouldContain("typeof(global::Test.WidgetCreated)");
        generated.ShouldNotContain("typeof(global::Test.WidgetStatus)");
    }
}

