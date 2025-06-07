using JasperFx;
using JasperFx.CommandLine;
using Microsoft.Extensions.DependencyInjection;

[assembly:JasperFxAssembly]

namespace FakeApp;

public class TestCommand : JasperFxAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        using var host = input.BuildHost();
        await host.StartAsync();

        var options = host.Services.GetRequiredService<JasperFxOptions>();
        
        Console.WriteLine("Found ApplicationAssembly: " + options.ApplicationAssembly);

        if (options.ApplicationAssembly.GetName().Name != "FakeApp")
        {
            throw new Exception("GOT THE WRONG ASSEMBLY. WAS: " + options.ApplicationAssembly.GetName().Name);
        }

        return true;
    }
}