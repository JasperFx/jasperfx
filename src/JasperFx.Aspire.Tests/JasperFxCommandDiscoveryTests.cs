using JasperFx.Aspire;
using Shouldly;

namespace JasperFx.Aspire.Tests;

public class JasperFxCommandDiscoveryTests
{
    // Mirrors the real `dotnet run -- help --json` stdout: framework noise precedes the JSON array.
    private const string NoisyOutput = """
        Searching 'JasperFx, Version=2.3.0.0, Culture=neutral, PublicKeyToken=null' for commands
        Searching 'Api, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null' for commands

        [
          { "name": "check-env", "description": "Execute all environment checks" },
          { "name": "resources", "description": "Stateful resources" },
          { "name": "run", "description": "Start and run" },
          { "name": "help", "description": "List commands" }
        ]
        """;

    [Fact]
    public void parses_command_names_from_noisy_output()
    {
        var names = JasperFxCommandDiscovery.ParseCatalog(NoisyOutput);

        names.ShouldBe(["check-env", "resources", "run", "help"]);
    }

    [Fact]
    public void empty_output_yields_no_names()
    {
        JasperFxCommandDiscovery.ParseCatalog("").ShouldBeEmpty();
        JasperFxCommandDiscovery.ParseCatalog("   ").ShouldBeEmpty();
    }

    [Fact]
    public void output_with_no_json_array_yields_no_names()
    {
        JasperFxCommandDiscovery.ParseCatalog("Searching for commands but it crashed").ShouldBeEmpty();
    }

    [Fact]
    public void malformed_json_yields_no_names()
    {
        JasperFxCommandDiscovery.ParseCatalog("[ { \"name\": ").ShouldBeEmpty();
    }

    [Fact]
    public void entries_without_a_name_are_skipped()
    {
        var names = JasperFxCommandDiscovery.ParseCatalog("""[ { "description": "no name" }, { "name": "describe" } ]""");

        names.ShouldBe(["describe"]);
    }
}
