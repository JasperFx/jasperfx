using Microsoft.Extensions.DependencyInjection;
using Oakton.Environment;

namespace JasperFx.CodeGeneration.Commands;

public static class VerificationExtensions
{
    /// <summary>
    ///     Add an environment check that all expected pre-built generated
    ///     types exist in the configured assembly
    /// </summary>
    /// <param name="services"></param>
    public static void AssertAllExpectedPreBuiltTypesExistOnStartUp(this IServiceCollection services)
    {
        services.AddSingleton<IEnvironmentCheck, AllPreGeneratedTypesExist>();
    }
}