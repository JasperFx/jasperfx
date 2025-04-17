using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace JasperFx.Environment;

/// <summary>
///     Executes the environment checks registered in an IoC container
/// </summary>
public static class EnvironmentChecker
{
    public static async Task<EnvironmentCheckResults> ExecuteAllEnvironmentChecks(IServiceProvider services,
        CancellationToken token = default)
    {
        var results = new EnvironmentCheckResults();
        var parts = services.GetServices<ISystemPart>().ToArray();

        foreach (var part in parts)
        {
            try
            {
                await part.AssertEnvironmentAsync(services, results, token);
                results.RegisterSuccess(part.Title);
            }
            catch (Exception e)
            {
                results.RegisterFailure(part.Title, e);
            }

            try
            {
                var resources = await part.FindResources();
                foreach (var resource in resources)
                {
                    try
                    {
                        await resource.Check(token);
                        results.RegisterSuccess(resource.SubjectUri.ToString());
                    }
                    catch (Exception e)
                    {
                        results.RegisterFailure($"{resource.SubjectUri} ({resource.ResourceUri})", e);
                    }
                }
            }
            catch (Exception e)
            {
                results.RegisterFailure(part.Title + " - Finding Resources", e);
            }
        }

        return results;
    }
}