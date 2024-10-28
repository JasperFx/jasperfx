using JasperFx.Environment;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Commands;

public class AllPreGeneratedTypesExist : IEnvironmentCheck
{
    public Task Assert(IServiceProvider services, CancellationToken cancellation)
    {
        var collections = services.GetServices<ICodeFileCollection>().ToArray();
        foreach (var collection in collections) collection.AssertPreBuildTypesExist(services);

        return Task.CompletedTask;
    }

    public string Description { get; } =
        "Asserting that all expected pre-built generated types exist in the configured assembly";
}