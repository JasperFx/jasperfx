using JasperFx.Aspire;
using Shouldly;

namespace JasperFx.Aspire.Tests;

public class JasperFxStartupHelperTests
{
    [Theory]
    [InlineData("resources", "setup")]
    [InlineData("codegen", "write")]
    [InlineData("check-env", null)]
    [InlineData("projections", null)]
    public void default_gate_arguments(string verb, string? expected)
    {
        JasperFxAspireStartupExtensions.DefaultGateArguments(verb).ShouldBe(expected);
    }

    [Fact]
    public void build_gate_name_includes_parent_verb_and_argument()
    {
        JasperFxAspireStartupExtensions.BuildGateName("api", "resources", "setup")
            .ShouldBe("api-resources-setup");
    }

    [Fact]
    public void build_gate_name_with_no_argument()
    {
        JasperFxAspireStartupExtensions.BuildGateName("api", "check-env", null)
            .ShouldBe("api-check-env");
    }

    [Fact]
    public void build_gate_name_is_lowercased()
    {
        JasperFxAspireStartupExtensions.BuildGateName("Api", "Resources", "Setup")
            .ShouldBe("api-resources-setup");
    }

    [Fact]
    public void build_args_prepends_the_verb()
    {
        JasperFxAspireStartupExtensions.BuildArgs("resources", "setup")
            .ShouldBe(["resources", "setup"]);
    }

    [Fact]
    public void build_args_with_no_argument()
    {
        JasperFxAspireStartupExtensions.BuildArgs("check-env", null)
            .ShouldBe(["check-env"]);
    }

    [Fact]
    public void build_args_splits_multi_token_arguments()
    {
        JasperFxAspireStartupExtensions.BuildArgs("projections", "rebuild Orders")
            .ShouldBe(["projections", "rebuild", "Orders"]);
    }
}
