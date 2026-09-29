using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Parsing;
using JasperFx.Resources;
using Shouldly;

namespace CommandLineTests.Resources;

// GH-921: --timeout and --type both used to claim -t
public class ResourceInputFlagTests
{
    private static ResourceInput build(params string[] tokens)
    {
        var usages = new ResourcesCommand().Usages;
        return (ResourceInput)usages.BuildInput(new Queue<string>(tokens), new ActivatorCommandCreator());
    }

    [Fact]
    public void short_t_is_the_type_filter()
    {
        var input = build("setup", "-t", "Wolverine");
        input.TypeFlag.ShouldBe("Wolverine");
        input.TimeoutFlag.ShouldBe(60);
    }

    [Fact]
    public void timeout_is_available_by_long_form()
    {
        var input = build("setup", "--timeout", "5", "--type", "Wolverine");
        input.TimeoutFlag.ShouldBe(5);
        input.TypeFlag.ShouldBe("Wolverine");
    }

    [Fact]
    public void no_two_resource_flags_share_a_short_form()
    {
        var shortForms = typeof(ResourceInput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(x => x.Name.EndsWith("Flag"))
            .Select(InputParser.ToFlagAliases)
            .Where(x => !x.LongFormOnly)
            .Select(x => x.ShortForm)
            .ToArray();

        shortForms.Distinct().Count().ShouldBe(shortForms.Length);
    }
}
