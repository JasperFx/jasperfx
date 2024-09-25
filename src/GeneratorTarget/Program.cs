using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton;

[assembly: OaktonCommandAssembly]

namespace GeneratorTarget;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ICodeFileCollection>(new GreeterGenerator());
                services.AddSingleton<ICodeFileCollection>(new GreeterGenerator2());

                services.AssertAllExpectedPreBuiltTypesExistOnStartUp();
            })
            .RunOaktonCommands(args);
    }
}