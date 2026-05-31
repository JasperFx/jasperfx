using System.Reflection;
using System.Text.Json;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Commands;
using JasperFx.CommandLine.Help;
using Shouldly;

namespace CommandLineTests;

public class CommandCatalogJsonTests
{
    private static IEnumerable<Type> BuiltInCommandTypes()
    {
        var factory = new CommandFactory();
        factory.RegisterCommands(typeof(RunCommand).GetTypeInfo().Assembly);
        return factory.AllCommandTypes();
    }

    [Fact]
    public void emits_a_json_array_of_name_and_description_objects()
    {
        var json = HelpCommand.ToCommandCatalogJson(BuiltInCommandTypes());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);

        var entries = doc.RootElement.EnumerateArray().ToArray();
        entries.Length.ShouldBeGreaterThan(0);
        foreach (var entry in entries)
        {
            entry.TryGetProperty("name", out _).ShouldBeTrue();
            entry.TryGetProperty("description", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public void includes_the_built_in_verbs_with_their_descriptions()
    {
        var json = HelpCommand.ToCommandCatalogJson(BuiltInCommandTypes());
        using var doc = JsonDocument.Parse(json);

        var byName = doc.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("description").GetString()!);

        byName.ShouldContainKey("check-env");
        byName.ShouldContainKey("describe");
        byName.ShouldContainKey("help");
        byName["check-env"].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void entries_are_sorted_by_command_name()
    {
        var json = HelpCommand.ToCommandCatalogJson(BuiltInCommandTypes());
        using var doc = JsonDocument.Parse(json);

        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();

        names.ShouldBe(names.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void empty_catalog_is_an_empty_json_array()
    {
        var json = HelpCommand.ToCommandCatalogJson([]);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBe(0);
    }
}
