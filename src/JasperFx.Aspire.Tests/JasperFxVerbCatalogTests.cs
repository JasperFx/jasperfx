using JasperFx.Aspire;
using Shouldly;

namespace JasperFx.Aspire.Tests;

public class JasperFxVerbCatalogTests
{
    [Fact]
    public void default_selection_is_the_read_only_verbs_only()
    {
        var options = new JasperFxCommandOptions();

        var verbs = JasperFxVerbCatalog.Resolve(options).Select(x => x.Verb).ToArray();

        verbs.ShouldBe(["check-env", "describe", "codegen"], ignoreOrder: true);
        JasperFxVerbCatalog.Resolve(options).ShouldAllBe(t => !t.Mutating);
    }

    [Fact]
    public void include_mutating_adds_the_mutating_verbs()
    {
        var options = new JasperFxCommandOptions { IncludeMutatingCommands = true };

        var keys = JasperFxVerbCatalog.Resolve(options).Select(x => x.Key).ToArray();

        keys.ShouldContain("jasperfx-check-env");
        keys.ShouldContain("jasperfx-codegen-preview");
        keys.ShouldContain("jasperfx-codegen-write");
        keys.ShouldContain("jasperfx-resources");
        keys.ShouldContain("jasperfx-projections");
    }

    [Fact]
    public void include_verbs_is_an_explicit_allow_list_overriding_defaults()
    {
        var options = new JasperFxCommandOptions();
        options.IncludeVerbs.Add("resources");   // a mutating verb, with no IncludeMutatingCommands

        var verbs = JasperFxVerbCatalog.Resolve(options).Select(x => x.Verb).ToArray();

        verbs.ShouldBe(["resources"]);
    }

    [Fact]
    public void exclude_verbs_removes_from_the_selection()
    {
        var options = new JasperFxCommandOptions();
        options.ExcludeVerbs.Add("describe");

        var verbs = JasperFxVerbCatalog.Resolve(options).Select(x => x.Verb).ToArray();

        verbs.ShouldNotContain("describe");
        verbs.ShouldContain("check-env");
    }

    [Fact]
    public void mutating_verbs_carry_a_confirmation_template()
    {
        foreach (var template in JasperFxVerbCatalog.Mutating)
        {
            template.ConfirmationMessage.ShouldNotBeNull();
            template.ConfirmationMessage!.ShouldContain("{0}");   // filled with the resource name
        }
    }

    [Fact]
    public void read_only_verbs_have_no_confirmation()
    {
        JasperFxVerbCatalog.ReadOnly.ShouldAllBe(t => t.ConfirmationMessage == null);
    }

    [Fact]
    public void template_for_known_verb_returns_catalog_entry()
    {
        var template = JasperFxVerbCatalog.TemplateFor("check-env", null);

        template.Key.ShouldBe("jasperfx-check-env");
        template.Mutating.ShouldBeFalse();
    }

    [Fact]
    public void template_for_known_verb_overrides_arguments()
    {
        var template = JasperFxVerbCatalog.TemplateFor("resources", "teardown");

        template.Verb.ShouldBe("resources");
        template.Arguments.ShouldBe("teardown");
    }

    [Fact]
    public void template_for_unknown_verb_is_mutating_with_confirmation()
    {
        var template = JasperFxVerbCatalog.TemplateFor("storage", "rebuild");

        template.Verb.ShouldBe("storage");
        template.Arguments.ShouldBe("rebuild");
        template.Mutating.ShouldBeTrue();
        template.ConfirmationMessage.ShouldNotBeNull();
        template.Key.ShouldBe("jasperfx-storage-rebuild");
    }
}
