using ExtensionStandIn;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using RunnerFrame = xunit.v3.stackwalk.standin.RunnerFrame;

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
            .StartAsync(TestContext.Current.CancellationToken);
        
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
            .StartAsync(TestContext.Current.CancellationToken);

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
            .StartAsync(TestContext.Current.CancellationToken);

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
    public void resolve_project_root_returns_null_for_nonexistent_path()
    {
        // Non-existent path should return null gracefully without throwing
        var result = JasperFxOptions.ResolveProjectRoot("/nonexistent/fake/path/xyz123");
        result.ShouldBeNull();
    }
    
    [Fact]
    public void resolve_project_root_finds_csproj_directory()
    {
        // Use the actual project structure for testing
        // We know we're running from somewhere like src/CoreTests/bin/Debug/net9.0
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
            
            var testPath = "/some/bin/Debug/net9.0";
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
            
            var testPath = "/some/bin/Debug/net9.0";
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

    // GH-3521: the application assembly is a process-wide value pinned by whichever host starts FIRST in the
    // process. A later host that adopts it while it was registered from a DIFFERENT assembly silently scans
    // the wrong assembly. These tests pin the behavior of the divergence detection surfaced on JasperFxOptions.

    [Fact]
    public void warns_when_adopted_assembly_diverges_from_where_the_host_registered()
    {
        var original = JasperFxOptions.RememberedApplicationAssembly;
        try
        {
            // Simulate an earlier host in the process having pinned a different assembly...
            var pinned = typeof(JasperFxOptions).Assembly;
            JasperFxOptions.RememberedApplicationAssembly = pinned;

            var options = new JasperFxOptions
            {
                // ...while THIS host was registered from the test assembly.
                RegistrationCallingAssembly = GetType().Assembly
            };

            options.ReadHostEnvironment(new StubHostEnvironment());

            options.ApplicationAssembly.ShouldBe(pinned);
            options.ApplicationAssemblyReuseWarning.ShouldNotBeNull();
            options.ApplicationAssemblyReuseWarning.ShouldContain(pinned.GetName().Name!);
            options.ApplicationAssemblyReuseWarning.ShouldContain(GetType().Assembly.GetName().Name!);
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = original;
        }
    }

    [Fact]
    public void does_not_warn_when_the_adopted_assembly_matches_where_the_host_registered()
    {
        var original = JasperFxOptions.RememberedApplicationAssembly;
        try
        {
            JasperFxOptions.RememberedApplicationAssembly = GetType().Assembly;

            var options = new JasperFxOptions
            {
                RegistrationCallingAssembly = GetType().Assembly
            };

            options.ReadHostEnvironment(new StubHostEnvironment());

            options.ApplicationAssemblyReuseWarning.ShouldBeNull();
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = original;
        }
    }

    [Fact]
    public void does_not_warn_when_the_registration_assembly_could_not_be_resolved()
    {
        var original = JasperFxOptions.RememberedApplicationAssembly;
        try
        {
            JasperFxOptions.RememberedApplicationAssembly = typeof(JasperFxOptions).Assembly;

            // RegistrationCallingAssembly left null — we don't warn on a value we couldn't attribute.
            var options = new JasperFxOptions();

            options.ReadHostEnvironment(new StubHostEnvironment());

            options.ApplicationAssemblyReuseWarning.ShouldBeNull();
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = original;
        }
    }

    [Fact]
    public void does_not_warn_when_the_application_assembly_is_set_explicitly()
    {
        var original = JasperFxOptions.RememberedApplicationAssembly;
        try
        {
            JasperFxOptions.RememberedApplicationAssembly = typeof(JasperFxOptions).Assembly;

            var options = new JasperFxOptions
            {
                // An explicit choice short-circuits establishApplicationAssembly entirely.
                ApplicationAssembly = GetType().Assembly,
                RegistrationCallingAssembly = GetType().Assembly
            };

            options.ReadHostEnvironment(new StubHostEnvironment());

            options.ApplicationAssemblyReuseWarning.ShouldBeNull();
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = original;
        }
    }

    [Fact]
    public async Task a_normal_single_assembly_host_does_not_warn()
    {
        // False-positive guard: a real host registered and resolved from the test assembly must not warn,
        // and the registration assembly must be captured as THIS assembly (not "JasperFx").
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(s => s.AddJasperFx())
            .UseEnvironment("Development")
            .StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<JasperFxOptions>();

        options.RegistrationCallingAssembly.ShouldBe(GetType().Assembly);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();
    }

    // GH-600: DetermineCallingAssembly walks out from JasperFx to the first frame that isn't System*,
    // Microsoft* or a test runner. Under an async fixture the intervening frames belong to the runner, so
    // without the runner filter the walk adopts something like "xunit.v3.core" and every consumer scans an
    // assembly containing none of the suite's types.

    [Theory]
    [InlineData("xunit.v3.core")]
    [InlineData("xunit.execution.dotnet")]
    [InlineData("xunit.runner.utility.netcoreapp10")]
    [InlineData("nunit.framework")]
    [InlineData("NUnit3.TestAdapter")]
    [InlineData("TUnit.Engine")]
    [InlineData("MSTest.TestAdapter")]
    [InlineData("testhost")]
    [InlineData("ReSharperTestRunner64")]
    [InlineData("JetBrains.ReSharper.TestRunner.Merged")]
    [InlineData("NCrunch.TestHost")]
    public void recognizes_test_runner_assemblies(string assemblyName)
    {
        JasperFxOptions.IsTestRunnerAssembly(assemblyName).ShouldBeTrue();
    }

    [Theory]
    [InlineData("CoreTests")]
    [InlineData("MyApp")]
    [InlineData("MyApp.Tests")]
    [InlineData("Wolverine")]
    [InlineData("Marten")]
    // Guard against over-eager prefixes: an ordinary application assembly must never be mistaken for a
    // runner just because its name happens to start with a runner-ish word.
    [InlineData("JetBrainsFanClub")]
    [InlineData("NCrunchyGranola")]
    public void does_not_mistake_application_assemblies_for_test_runners(string assemblyName)
    {
        JasperFxOptions.IsTestRunnerAssembly(assemblyName).ShouldBeFalse();
    }

    [Fact]
    public void walks_past_a_test_runner_frame_out_to_the_test_assembly()
    {
        // RunnerFrame lives in an assembly named "xunit.v3.stackwalk.standin", so calling through it puts
        // a runner-named frame between JasperFx and this test -- the layout an async test fixture produces
        // for real, where the frames above JasperFx belong to the runner rather than the test assembly.
        // Before the fix the walk stopped on that frame and adopted the runner.
        var assembly = RunnerFrame.Invoke(JasperFxOptions.DetermineCallingAssembly);

        assembly.ShouldBe(GetType().Assembly);
    }

    [Fact]
    public void never_adopts_a_test_runner_as_the_calling_assembly()
    {
        var assembly = RunnerFrame.Invoke(JasperFxOptions.DetermineCallingAssembly);

        assembly.ShouldNotBeNull();
        JasperFxOptions.IsTestRunnerAssembly(assembly.GetName().Name!).ShouldBeFalse();
    }

    // GH-601: UseWolverine() and AddMarten() both call services.AddJasperFx() from inside their own
    // assembly, so the first frame outside JasperFx belongs to the extension rather than the application.

    [Theory]
    [InlineData("JasperFx")]
    [InlineData("JasperFx.Events")]
    [InlineData("JasperFx.RuntimeCompiler")]
    [InlineData("Wolverine")]
    [InlineData("Wolverine.SqlServer")]
    [InlineData("WolverineFx.RabbitMQ")]
    [InlineData("Marten")]
    [InlineData("Marten.AspNetCore")]
    [InlineData("Weasel.Postgresql")]
    [InlineData("Polecat")]
    [InlineData("CritterWatch.Server")]
    [InlineData("Oakton")]
    public void recognizes_critter_stack_framework_assemblies(string assemblyName)
    {
        JasperFxOptions.IsCritterStackAssembly(assemblyName).ShouldBeTrue();
    }

    [Theory]
    // An application is not framework code just because it is named for a product. Matching is by exact
    // name or dotted prefix, never a bare StartsWith.
    [InlineData("MartenPlayground")]
    [InlineData("WolverineDemo")]
    [InlineData("JasperFxSamples")]
    [InlineData("MyApp")]
    [InlineData("CoreTests")]
    // And the Critter Stack repos' own test assemblies ARE the application under test -- their handlers
    // and documents are the types discovery has to find.
    [InlineData("Wolverine.RabbitMQ.Tests")]
    [InlineData("Marten.Testing")]
    [InlineData("JasperFx.Events.Tests")]
    public void does_not_mistake_applications_or_suites_for_framework_assemblies(string assemblyName)
    {
        JasperFxOptions.IsCritterStackAssembly(assemblyName).ShouldBeFalse();
    }

    [Fact]
    public async Task registration_is_attributed_to_the_app_not_the_extension_that_registered_for_it()
    {
        var original = JasperFxOptions.RememberedApplicationAssembly;
        try
        {
            JasperFxOptions.RememberedApplicationAssembly = null;

            // AddSomeCritterStackTool lives in an assembly named "Wolverine.StackWalkStandIn" and calls
            // AddJasperFx() on our behalf, which is exactly what UseWolverine()/AddMarten() do.
            using var host = await Host.CreateDefaultBuilder()
                .ConfigureServices(s => s.AddSomeCritterStackTool())
                .UseEnvironment("Development")
                .StartAsync(TestContext.Current.CancellationToken);

            var options = host.Services.GetRequiredService<JasperFxOptions>();

            options.RegistrationCallingAssembly.ShouldBe(GetType().Assembly);

            // ...and because registration is now attributed correctly, the GH-3521 divergence warning
            // stays quiet on a host where nothing is actually wrong.
            options.ApplicationAssemblyReuseWarning.ShouldBeNull();
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = original;
        }
    }

    [Fact]
    public void an_extension_registration_does_not_pin_the_extension_as_the_application_assembly()
    {
        var original = JasperFxOptions.RememberedApplicationAssembly;
        try
        {
            JasperFxOptions.RememberedApplicationAssembly = null;

            // No meaningful IHostEnvironment.ApplicationName here, so establishApplicationAssembly falls
            // through to the process-wide pin that AddJasperFx seeded from the same stack walk. Before the
            // fix that pin -- and therefore type discovery -- was the extension assembly.
            var services = new ServiceCollection();
            services.AddSomeCritterStackTool();

            var options = services.BuildServiceProvider().GetRequiredService<JasperFxOptions>();

            options.ApplicationAssembly.ShouldBe(GetType().Assembly);
            JasperFxOptions.RememberedApplicationAssembly.ShouldBe(GetType().Assembly);
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = original;
        }
    }
}