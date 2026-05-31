using JasperFx.Aspire;
using Shouldly;

namespace JasperFx.Aspire.Tests;

public class JasperFxCommandExecutorTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public void build_start_info_runs_dotnet_run_with_no_build_and_the_verb()
    {
        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "check-env", null, NoEnv);

        psi.FileName.ShouldBe("dotnet");
        psi.WorkingDirectory.ShouldBe("/code/api");
        psi.ArgumentList.ShouldBe(["run", "--project", "/code/api/Api.csproj", "--no-build", "--", "check-env"]);
        psi.RedirectStandardOutput.ShouldBeTrue();
        psi.RedirectStandardError.ShouldBeTrue();
        psi.UseShellExecute.ShouldBeFalse();
    }

    [Fact]
    public void build_start_info_appends_fixed_arguments()
    {
        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "resources", "setup", NoEnv);

        psi.ArgumentList.ShouldBe(
            ["run", "--project", "/code/api/Api.csproj", "--no-build", "--", "resources", "setup"]);
    }

    [Fact]
    public void build_start_info_splits_multi_token_arguments()
    {
        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "projections", "rebuild Orders", NoEnv);

        psi.ArgumentList.ShouldBe(
            ["run", "--project", "/code/api/Api.csproj", "--no-build", "--", "projections", "rebuild", "Orders"]);
    }

    [Fact]
    public void build_start_info_applies_resolved_environment()
    {
        var env = new Dictionary<string, string>
        {
            ["ConnectionStrings__postgres"] = "Host=localhost;Port=5432",
            ["ASPNETCORE_ENVIRONMENT"] = "Development"
        };

        var psi = JasperFxCommandExecutor.BuildStartInfo(
            "/code/api/Api.csproj", "/code/api", "check-env", null, env);

        psi.Environment["ConnectionStrings__postgres"].ShouldBe("Host=localhost;Port=5432");
        psi.Environment["ASPNETCORE_ENVIRONMENT"].ShouldBe("Development");
    }

    [Fact]
    public void map_result_success_on_zero_exit_code()
    {
        var result = JasperFxCommandExecutor.MapResult("check-env", 0, "all good");

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public void map_result_failure_on_nonzero_exit_code_includes_verb_code_and_tail()
    {
        var result = JasperFxCommandExecutor.MapResult("resources", 1, "could not connect to database");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("resources");
        result.Message.ShouldContain("1");
        result.Message.ShouldContain("could not connect to database");
    }

    [Fact]
    public void map_result_failure_with_no_output_still_reports_the_exit_code()
    {
        var result = JasperFxCommandExecutor.MapResult("describe", 3, "");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("3");
    }
}
