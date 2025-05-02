using JasperFx.CommandLine.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CommandLineTests;

public class run_command_compliance
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("=")]
    [InlineData("a=")]
    [InlineData("=b")]
    [InlineData("a=b=")]
    public void set_invalid_values_on_environment_flag(string? value)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            new RunInput().EnvironmentFlag = value;
        });
    }

    [Fact]
    public void use_environment_flag_once()
    {
        var input = new RunInput();
        input.EnvironmentFlag = "one=blue";
        
        System.Environment.GetEnvironmentVariable("one").ShouldBe("blue");
    }

    [Fact]
    public void use_environment_variable_multiple_times()
    {
        var input = new RunInput();
        input.EnvironmentFlag = "one=blue";
        input.EnvironmentFlag = "two=green";
        
        System.Environment.GetEnvironmentVariable("one").ShouldBe("blue");
        System.Environment.GetEnvironmentVariable("two").ShouldBe("green");
    }

    [Fact]
    public void set_the_environment_name()
    {
        var input = new RunInput();
        input.EnvironmentFlag = "Testing";
        
        input.EnvironmentNameFlag.ShouldBe("Testing");
    }

    [Fact]
    public void override_the_environment_name()
    {
        var input = new RunInput
        {
            EnvironmentNameFlag = Guid.NewGuid().ToString(),
            HostBuilder = Host.CreateDefaultBuilder()
        };

        var host = input.BuildHost();
        host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.ShouldBe(input.EnvironmentNameFlag);
    }

    [Fact]
    public void override_the_content_root()
    {
        var input = new RunInput
        {
            ContentRootFlag = "/bin",
            HostBuilder = Host.CreateDefaultBuilder()
        };
        
        var host = input.BuildHost();
        host.Services.GetRequiredService<IHostEnvironment>().ContentRootPath.ShouldBe("/bin");
    }

    [Fact]
    public void override_the_application_name()
    {
        var input = new RunInput
        {
            ApplicationNameFlag = "ThisApp",
            HostBuilder = Host.CreateDefaultBuilder()
        };
        
        var host = input.BuildHost();
        host.Services.GetRequiredService<IHostEnvironment>().ApplicationName.ShouldBe("ThisApp");
    }
}