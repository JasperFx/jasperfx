using System.Runtime.CompilerServices;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Commands;
using JasperFx.CommandLine.Descriptions;
using JasperFx.CommandLine.Help;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CommandLineTests;

/// <summary>
/// jasperfx#1141. The [ModuleInitializer] that JasperFx.SourceGenerator emits beside each
/// DiscoveredCommands manifest registers the commands, `new T()` factories for them and their
/// inputs, and [InjectService] injectors, so a Native AOT binary needs neither the by-name manifest
/// lookup nor Activator.CreateInstance nor Type.GetProperties(). The native binary itself is proven
/// by ./build.sh smoke-test-aot-cli; these pin down that the generated path is the one in use.
/// </summary>
public class GeneratedCommandRegistryTests
{
    public GeneratedCommandRegistryTests()
    {
        // Under the JIT a module initializer runs on first touch of its module; CommandFactory
        // forces it the same way before it reads the registry.
        RuntimeHelpers.RunModuleConstructor(typeof(RunCommand).Assembly.ManifestModule.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(InjectedCommand).Assembly.ManifestModule.ModuleHandle);
    }

    [Fact]
    public void builtin_commands_are_registered_by_the_generated_module_initializer()
    {
        var commands = GeneratedCommandRegistry.CommandTypesFor(typeof(RunCommand).Assembly);

        commands.ShouldNotBeNull();
        commands.ShouldContain(typeof(RunCommand));
        commands.ShouldContain(typeof(HelpCommand));
        commands.ShouldContain(typeof(DescribeCommand));
        commands.ShouldContain(typeof(CheckEnvironmentCommand));
    }

    [Fact]
    public void application_commands_are_registered_by_the_generated_module_initializer()
    {
        GeneratedCommandRegistry.CommandTypesFor(typeof(InjectedCommand).Assembly)
            .ShouldNotBeNull()
            .ShouldContain(typeof(InjectedCommand));
    }

    [Theory]
    [InlineData(typeof(RunCommand))]
    [InlineData(typeof(HelpCommand))]
    [InlineData(typeof(RunInput))]
    [InlineData(typeof(HelpInput))]
    [InlineData(typeof(InjectedCommand))]
    [InlineData(typeof(InjectedInput))]
    public void commands_and_inputs_are_built_by_generated_factories(Type type)
    {
        GeneratedCommandRegistry.TryCreate(type, out var instance).ShouldBeTrue();
        instance.ShouldBeOfType(type);
    }

    [Fact]
    public void the_command_creator_uses_the_generated_factory()
    {
        new ActivatorCommandCreator().CreateCommand(typeof(RunCommand)).ShouldBeOfType<RunCommand>();
        new ActivatorCommandCreator().CreateModel(typeof(RunInput)).ShouldBeOfType<RunInput>();
    }

    [Fact]
    public void inject_service_properties_are_set_by_a_generated_injector()
    {
        GeneratedCommandRegistry.TryGetInjector(typeof(InjectedCommand), out var inject).ShouldBeTrue();

        var services = new ServiceCollection()
            .AddScoped<MyService>()
            .AddScoped<OtherService>()
            .BuildServiceProvider();

        var command = new InjectedCommand();
        inject(command, services);

        command.One.ShouldNotBeNull();
        command.Two.ShouldNotBeNull();
    }

    [Fact]
    public void the_generated_injector_requires_the_service_like_GetRequiredService()
    {
        GeneratedCommandRegistry.TryGetInjector(typeof(InjectedCommand), out var inject).ShouldBeTrue();

        var services = new ServiceCollection().BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => inject(new InjectedCommand(), services))
            .Message.ShouldContain("CommandLineTests.MyService");
    }

    [Fact]
    public void commands_without_inject_service_properties_get_no_injector()
    {
        GeneratedCommandRegistry.TryGetInjector(typeof(RunCommand), out _).ShouldBeFalse();
    }

    [Fact]
    public void usage_graph_built_from_a_known_input_type_matches_the_reflective_one()
    {
        // The command base classes now pass typeof(T) instead of walking the command type's
        // interfaces, whose metadata a Native AOT image does not keep.
        var known = new UsageGraph(typeof(RunCommand), typeof(RunInput));
        var walked = new UsageGraph(typeof(RunCommand));

        known.CommandName.ShouldBe(walked.CommandName);
        known.Description.ShouldBe(walked.Description);
        known.Handlers.Count().ShouldBe(walked.Handlers.Count());
    }
}
