using JasperFx.CodeGeneration;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CodegenTests.Compilation;

public class runtime_compilation_registration
{
    [Fact]
    public void AddRuntimeCompilation_registers_AssemblyGenerator_as_IAssemblyGenerator()
    {
        var services = new ServiceCollection();
        services.AddRuntimeCompilation();

        var sp = services.BuildServiceProvider();
        var generator = sp.GetRequiredService<IAssemblyGenerator>();

        generator.ShouldBeOfType<AssemblyGenerator>();
    }

    [Fact]
    public void AddRuntimeCompilation_resolves_as_singleton()
    {
        var services = new ServiceCollection();
        services.AddRuntimeCompilation();

        var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredService<IAssemblyGenerator>();
        var second = sp.GetRequiredService<IAssemblyGenerator>();

        first.ShouldBeSameAs(second);
    }
}
