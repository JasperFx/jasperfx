using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace CoreTests;

public class registering_the_options
{
    private readonly IHostEnvironment theEnvironment = Substitute.For<IHostEnvironment>();
    
    [Fact]
    public void register_all_defaults()
    {
        var services = new ServiceCollection();
        services.JasperFxDefaults();
        services.AddSingleton(theEnvironment);

        theEnvironment.EnvironmentName.Returns("Production");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<JasperFxOptions>();
        
        options.ActiveProfile.AutoCreate.ShouldBe(AutoCreate.CreateOrUpdate);
        options.ActiveProfile.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        options.ActiveProfile.SourceCodeWritingEnabled.ShouldBe(true);
        options.ActiveProfile.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
    }

    [Fact]
    public void pick_up_development_mode()
    {
        var services = new ServiceCollection();
        services.JasperFxDefaults(x =>
        {
            x.Development.AutoCreate = AutoCreate.All;
        });
        
        services.AddSingleton(theEnvironment);

        theEnvironment.EnvironmentName.Returns("Development");
        
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<JasperFxOptions>();
        
        options.ActiveProfile.ShouldBeSameAs(options.Development);
        
        // Just seeing that we're applying the configure
        options.ActiveProfile.AutoCreate.ShouldBe(AutoCreate.All);
    }
    
    [Fact]
    public void pick_up_development_mode_with_alternative_environment_name()
    {
        var services = new ServiceCollection();
        services.JasperFxDefaults(x =>
        {
            x.Development.AutoCreate = AutoCreate.All;
            x.DevelopmentEnvironmentName = "weird";
        });
        
        services.AddSingleton(theEnvironment);

        theEnvironment.EnvironmentName.Returns("Weird");
        
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<JasperFxOptions>();
        
        options.ActiveProfile.ShouldBeSameAs(options.Development);
        
        // Just seeing that we're applying the configure
        options.ActiveProfile.AutoCreate.ShouldBe(AutoCreate.All);
    }
    
    
    
    [Fact]
    public void pick_up_production_mode()
    {
        var services = new ServiceCollection();
        services.JasperFxDefaults(x =>
        {
            x.Development.AutoCreate = AutoCreate.All;

            x.Production.AutoCreate = AutoCreate.None;
            x.Production.GeneratedCodeMode = TypeLoadMode.Static;
            x.Production.AssertAllPreGeneratedTypesExist = true;
        });
        
        services.AddSingleton(theEnvironment);

        theEnvironment.EnvironmentName.Returns("Production");
        
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<JasperFxOptions>();
        
        options.ActiveProfile.ShouldBeSameAs(options.Production);
        
        // Just seeing that we're applying the configure
        options.ActiveProfile.AutoCreate.ShouldBe(AutoCreate.None);
        options.ActiveProfile.GeneratedCodeMode.ShouldBe(TypeLoadMode.Static);
    }
    
    [Fact]
    public void application_assembly_and_content_directory_from_StoreOptions()
    {
        using var host = Host.CreateDefaultBuilder(Array.Empty<string>())
            .ConfigureServices(services =>
            {
                services.JasperFxDefaults(opts =>
                {
                    opts.SetApplicationProject(GetType().Assembly);
                });
            }).Build();

        var options = host.Services.GetRequiredService<JasperFxOptions>();
        
        options.ApplicationAssembly.ShouldBe(GetType().Assembly);
        var projectPath = AppContext.BaseDirectory.ParentDirectory().ParentDirectory().ParentDirectory();
        var expectedGeneratedCodeOutputPath = projectPath.ToFullPath().AppendPath("Internal", "Generated");
        options.GeneratedCodeOutputPath.ShouldBe(expectedGeneratedCodeOutputPath);
    }

    [Fact]
    public void build_from_host()
    {
        var builder = Host.CreateApplicationBuilder();
        
        // This would apply to both Marten, Wolverine, and future critters....
        builder.Services.JasperFxDefaults(x =>
        {
            // This expands in importance to be the master "AutoCreate"
            // over every resource at runtime and not just databases
            // So this would maybe take the place of AutoProvision() in Wolverine world too
            x.Production.AutoCreate = AutoCreate.None;
            x.Production.GeneratedCodeMode = TypeLoadMode.Static;
            x.Production.AssertAllPreGeneratedTypesExist = true;
            
            // Just for completeness sake
            x.Development.AutoCreate = AutoCreate.All; // default is still CreateOrUpdate

            // Unify the Marten/Wolverine/future critter application assembly
            // Default will always be the entry assembly
            x.ApplicationAssembly = typeof(Message1).Assembly;
        });
        
        // keep bootstrapping...

        var host = builder.Build();
        host.Services.GetRequiredService<JasperFxOptions>().Production.AutoCreate.ShouldBe(AutoCreate.None);
    }
}

public class Message1{}