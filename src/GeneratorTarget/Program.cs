using System.Threading.Tasks;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Commands;
using JasperFx.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: JasperFxAssembly]

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

                services.AddJasperFx(opts => opts.ActiveProfile.AssertAllPreGeneratedTypesExist = true);
                
            })
            .RunJasperFxCommands(args);
    }
}