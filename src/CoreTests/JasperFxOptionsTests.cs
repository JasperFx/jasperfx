using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
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
        options.Development.ResourceAutoCreate.ShouldBe(AutoCreate.CreateOrUpdate);
        options.Development.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        options.Development.SourceCodeWritingEnabled.ShouldBeTrue();
        options.Development.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
        
        options.DevelopmentEnvironmentName.ShouldBe("Development");
        
        options.Production.ResourceAutoCreate.ShouldBe(AutoCreate.CreateOrUpdate);
        options.Production.GeneratedCodeMode.ShouldBe(TypeLoadMode.Dynamic);
        options.Production.SourceCodeWritingEnabled.ShouldBeTrue();
        options.Production.AssertAllPreGeneratedTypesExist.ShouldBeFalse();
    }

    [Fact]
    public void read_environment_for_development()
    {
        var options = new JasperFxOptions();

        var environment = new StubHostEnvironment{EnvironmentName = options.DevelopmentEnvironmentName};
        
        options.ReadHostEnvironment(environment);
        
        options.ActiveProfile.ShouldBe(options.Development);
    }

    [Fact]
    public async Task read_application_assembly_correctly()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx())
            .UseEnvironment("Development")
            .StartAsync();
        
        var options = host.Services.GetRequiredService<JasperFxOptions>();
        
        options.ApplicationAssembly.ShouldBe(GetType().Assembly);
    }

    public class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "JasperFxApp";
        public string ContentRootPath { get; set; } = "./";
        public IFileProvider ContentRootFileProvider { get; set; } = Substitute.For<IFileProvider>();
    }
    
    [Fact]
    public void read_environment_for_production()
    {
        var options = new JasperFxOptions();

        var environment = new StubHostEnvironment { EnvironmentName = "Production" };
        
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
        
        host.Services.GetServices<ISystemPart>().OfType<JasperFxOptions>().Any().ShouldBeTrue();
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
    
    [Fact]
    public void auto_resolve_project_root_defaults_to_false()
    {
        var options = new JasperFxOptions();
        options.AutoResolveProjectRoot.ShouldBeFalse();
    }
    
    [Fact]
    public void resolve_project_root_returns_null_for_non_bin_path()
    {
        // Path that doesn't contain bin folder should return null
        var result = JasperFxOptions.ResolveProjectRoot("/some/project/path");
        result.ShouldBeNull();
    }
    
    [Fact]
    public void resolve_project_root_returns_null_for_path_without_bin_separator()
    {
        // Path that contains "bin" but not as a directory should return null
        var result = JasperFxOptions.ResolveProjectRoot("/some/binary/path");
        result.ShouldBeNull();
    }
    
    [Fact]
    public void resolve_project_root_finds_csproj_directory()
    {
        // Use the actual project structure for testing
        // We know we're running from somewhere like src/CoreTests/bin/Debug/net8.0
        var currentDir = AppContext.BaseDirectory;
        
        // Only run this test if we're in a bin folder
        if (!currentDir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
        {
            return; // Skip if not running from bin folder
        }
        
        var result = JasperFxOptions.ResolveProjectRoot(currentDir);
        
        result.ShouldNotBeNull();
        // The resolved path should contain a .csproj file
        Directory.GetFiles(result, "*.csproj").ShouldNotBeEmpty();
    }
    
    [Fact]
    public void read_host_environment_uses_resolved_path_during_codegen()
    {
        var options = new JasperFxOptions();
        options.AutoResolveProjectRoot = true;
        
        // Simulate being in a codegen command
        var originalValue = DynamicCodeBuilder.WithinCodegenCommand;
        try
        {
            DynamicCodeBuilder.WithinCodegenCommand = true;
            
            var currentDir = AppContext.BaseDirectory;
            var environment = new StubHostEnvironment { ContentRootPath = currentDir };
            
            options.ReadHostEnvironment(environment);
            
            // If we're in a bin folder and can resolve the project root,
            // the generated code output path should NOT contain bin
            if (currentDir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            {
                var resolvedRoot = JasperFxOptions.ResolveProjectRoot(currentDir);
                if (resolvedRoot != null)
                {
                    options.GeneratedCodeOutputPath.ShouldStartWith(resolvedRoot);
                    options.GeneratedCodeOutputPath.ShouldNotContain(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);
                }
            }
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = originalValue;
        }
    }
    
    [Fact]
    public void read_host_environment_uses_original_path_when_auto_resolve_disabled()
    {
        var options = new JasperFxOptions();
        options.AutoResolveProjectRoot = false;
        
        var originalValue = DynamicCodeBuilder.WithinCodegenCommand;
        try
        {
            DynamicCodeBuilder.WithinCodegenCommand = true;
            
            var testPath = "/some/bin/Debug/net8.0";
            var environment = new StubHostEnvironment { ContentRootPath = testPath };
            
            options.ReadHostEnvironment(environment);
            
            // Should use the original path since auto-resolve is disabled
            options.GeneratedCodeOutputPath.ShouldBe(testPath.AppendPath("Internal", "Generated"));
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = originalValue;
        }
    }
    
    [Fact]
    public void read_host_environment_uses_original_path_when_not_in_codegen_command()
    {
        var options = new JasperFxOptions();
        options.AutoResolveProjectRoot = true;
        
        var originalValue = DynamicCodeBuilder.WithinCodegenCommand;
        try
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
            
            var testPath = "/some/bin/Debug/net8.0";
            var environment = new StubHostEnvironment { ContentRootPath = testPath };
            
            options.ReadHostEnvironment(environment);
            
            // Should use the original path since we're not in a codegen command
            options.GeneratedCodeOutputPath.ShouldBe(testPath.AppendPath("Internal", "Generated"));
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = originalValue;
        }
    }
}