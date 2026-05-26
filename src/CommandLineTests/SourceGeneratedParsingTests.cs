using System.Reflection;
using JasperFx.CommandLine.Descriptions;
using JasperFx.CommandLine.Parsing;
using JasperFx.Resources;
using Shouldly;

namespace CommandLineTests;

/// <summary>
/// Guardrails ensuring the JasperFx.SourceGenerator output is actually wired in and used
/// for the framework's own commands. Regression cover for the bug where the generators
/// compared INamedTypeSymbol.ToDisplayString() (C# style "Foo&lt;T&gt;") against the metadata
/// form ("Foo`1"), which never matched, so the generators silently emitted nothing and every
/// command fell back to reflection + assembly scanning.
/// </summary>
public class SourceGeneratedParsingTests
{
    // Touch a JasperFx type so the assembly (and its [ModuleInitializer] parser registration) is loaded.
    private static readonly Assembly JasperFxAssembly = typeof(DescribeInput).Assembly;

    [Theory]
    [InlineData(typeof(DescribeInput))]
    [InlineData(typeof(ResourceInput))]
    public void generated_parser_is_registered_for_builtin_command_input(Type inputType)
    {
        GeneratedParserRegistry.HasParser(inputType)
            .ShouldBeTrue($"No source-generated parser registered for {inputType.Name}; " +
                          "the InputParserGenerator is not emitting/registering parsers.");
    }

    [Theory]
    [InlineData(typeof(DescribeInput))]
    [InlineData(typeof(ResourceInput))]
    public void generated_handlers_match_the_reflection_handlers(Type inputType)
    {
        var generated = GeneratedParserRegistry.TryGetHandlers(inputType);
        generated.ShouldNotBeNull();

        var reflected = InputParser.GetHandlers(inputType);

        // The generated path must cover exactly the same members as the reflection fallback.
        generated.Count.ShouldBe(reflected.Count);
    }

    [Fact]
    public void source_generated_command_manifest_is_present_and_populated()
    {
        var manifestType = JasperFxAssembly.GetType("JasperFx.Generated.DiscoveredCommands");
        manifestType.ShouldNotBeNull("CommandDiscoveryGenerator did not emit JasperFx.Generated.DiscoveredCommands");

        var property = manifestType.GetProperty("CommandTypes", BindingFlags.Public | BindingFlags.Static);
        property.ShouldNotBeNull();

        var commandTypes = ((IEnumerable<Type>)property.GetValue(null)!).ToList();
        commandTypes.ShouldContain(typeof(DescribeCommand));
        commandTypes.ShouldContain(typeof(ResourcesCommand));
    }
}
