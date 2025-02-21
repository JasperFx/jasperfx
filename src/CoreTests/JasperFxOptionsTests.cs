using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace CoreTests;

public class JasperFxOptionsTests
{
    [Fact]
    public void defaults()
    {
        var options = new JasperFxOptions();
        options.Development.AutoCreate.ShouldBe(AutoCreate.CreateOrUpdate);
        options.Development.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        options.Development.SourceCodeWritingEnabled.ShouldBeTrue();
        options.Development.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
        
        options.DevelopmentEnvironmentName.ShouldBe("Development");
        
        options.Production.AutoCreate.ShouldBe(AutoCreate.CreateOrUpdate);
        options.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        options.Production.SourceCodeWritingEnabled.ShouldBeTrue();
        options.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
    }

    [Fact]
    public void read_environment_for_development()
    {
        var options = new JasperFxOptions();

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(options.DevelopmentEnvironmentName);
        
        options.ReadHostEnvironment(environment);
        
        options.ActiveProfile.ShouldBe(options.Development);
    }
    
    [Fact]
    public void read_environment_for_production()
    {
        var options = new JasperFxOptions();

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        
        options.ReadHostEnvironment(environment);
        
        options.ActiveProfile.ShouldBe(options.Production);
    }

    [Fact]
    public async Task end_to_end_with_options_in_development_mode()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx())
            .UseEnvironment("Development")
            .StartAsync();

        var options = host.Services.GetRequiredService<JasperFxOptions>();
        options.ActiveProfile.ShouldBe(options.Development);
    }
    
    [Fact]
    public async Task end_to_end_with_options_in_production_mode()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx())
            .UseEnvironment("Production")
            .StartAsync();

        var options = host.Services.GetRequiredService<JasperFxOptions>();
        options.ActiveProfile.ShouldBe(options.Production);
    }
}