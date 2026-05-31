using JasperFx.Aspire;
using Shouldly;

namespace JasperFx.Aspire.Tests;

public class ResolveDiscoveredTests
{
    private static readonly string[] DiscoveredVerbs =
        ["check-env", "describe", "codegen", "resources", "projections", "storage", "run", "help"];

    private static string[] Verbs(JasperFxCommandOptions options)
        => JasperFxVerbCatalog.ResolveDiscovered(DiscoveredVerbs, options).Select(t => t.Verb).ToArray();

    [Fact]
    public void excludes_run_and_help_always()
    {
        var verbs = Verbs(new JasperFxCommandOptions { IncludeMutatingCommands = true });

        verbs.ShouldNotContain("run");
        verbs.ShouldNotContain("help");
    }

    [Fact]
    public void default_keeps_only_read_only_discovered_verbs()
    {
        var verbs = Verbs(new JasperFxCommandOptions());

        verbs.ShouldContain("check-env");
        verbs.ShouldContain("describe");
        verbs.ShouldContain("codegen");        // maps to read-only preview by default
        verbs.ShouldNotContain("resources");   // mutating
        verbs.ShouldNotContain("projections"); // mutating
        verbs.ShouldNotContain("storage");     // unknown → treated as mutating
    }

    [Fact]
    public void include_mutating_keeps_the_mutating_and_unknown_verbs()
    {
        var verbs = Verbs(new JasperFxCommandOptions { IncludeMutatingCommands = true });

        verbs.ShouldContain("resources");
        verbs.ShouldContain("projections");
        verbs.ShouldContain("storage"); // unknown product-specific verb surfaces when mutating is opted in
    }

    [Fact]
    public void unknown_verbs_are_treated_as_mutating()
    {
        var template = JasperFxVerbCatalog.ResolveDiscovered(["storage"],
            new JasperFxCommandOptions { IncludeMutatingCommands = true }).Single();

        template.Verb.ShouldBe("storage");
        template.Mutating.ShouldBeTrue();
        template.ConfirmationMessage.ShouldNotBeNull();
    }

    [Fact]
    public void include_verbs_is_an_explicit_allow_list()
    {
        var options = new JasperFxCommandOptions();
        options.IncludeVerbs.Add("resources");

        Verbs(options).ShouldBe(["resources"]);
    }

    [Fact]
    public void exclude_verbs_removes_from_the_selection()
    {
        var options = new JasperFxCommandOptions { IncludeMutatingCommands = true };
        options.ExcludeVerbs.Add("projections");

        Verbs(options).ShouldNotContain("projections");
    }
}
