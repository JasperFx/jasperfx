using JasperFx;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Commands;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Spectre.Console;

namespace CommandLineTests.Bugs;

[Collection("SetConsoleOutput")]
public class bug_986_prebuilt_host_environment_warning : IDisposable
{
    private const string TheWarning = "cannot override the environment name";

    private readonly StringWriter theOutput = new();
    private readonly IAnsiConsole theOriginalAnsiConsole;

    public bug_986_prebuilt_host_environment_warning()
    {
        theOriginalAnsiConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(theOutput)
        });
    }

    public void Dispose()
    {
        AnsiConsole.Console = theOriginalAnsiConsole;
    }

    private static IHost buildHost(string environment)
    {
        return Host.CreateDefaultBuilder()
            .UseEnvironment(environment)
            .Build();
    }

    [Fact]
    public void no_warning_when_the_flag_matches_the_prebuilt_host()
    {
        using var host = buildHost("Development");

        var input = new RunInput { EnvironmentFlag = "Development" };
        input.HostBuilder = new PreBuiltHostBuilder(host);

        theOutput.ToString().ShouldNotContain(TheWarning);
    }

    [Fact]
    public void no_warning_when_the_flag_matches_ignoring_case()
    {
        using var host = buildHost("Development");

        var input = new RunInput { EnvironmentFlag = "development" };
        input.HostBuilder = new PreBuiltHostBuilder(host);

        theOutput.ToString().ShouldNotContain(TheWarning);
    }

    [Fact]
    public void still_warns_when_the_flag_disagrees_with_the_prebuilt_host()
    {
        using var host = buildHost("Development");

        var input = new RunInput { EnvironmentFlag = "Staging" };
        input.HostBuilder = new PreBuiltHostBuilder(host);

        var output = theOutput.ToString();
        output.ShouldContain(TheWarning);
        output.ShouldContain("Staging");
        output.ShouldContain("Development");
    }

    [Fact]
    public void no_warning_for_the_args_web_application_factory_passes_to_main()
    {
        using var host = buildHost("Development");

        // DeferredHostBuilder serializes its host configuration into args like these
        string[] args =
        [
            "--environment=Development",
            "--contentRoot=" + AppContext.BaseDirectory,
            "--applicationName=CommandLineTests"
        ];

        var factory = new CommandFactory();
        factory.RegisterCommand<RunCommand>();

        var run = factory.BuildRun(args.ApplyArgumentDefaults(null));
        var input = run.Input.ShouldBeOfType<RunInput>();
        input.EnvironmentFlag.ShouldBe("Development");

        input.HostBuilder = new PreBuiltHostBuilder(host);

        theOutput.ToString().ShouldNotContain(TheWarning);
    }
}
