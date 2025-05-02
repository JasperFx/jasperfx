using JasperFx.CommandLine.Commands;
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
}