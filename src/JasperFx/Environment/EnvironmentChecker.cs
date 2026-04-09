using JasperFx.CommandLine.Descriptions;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

        // Execute any registered IHealthCheck instances
        await ExecuteHealthChecks(services, results, token);

        return results;
    }

    private static async Task ExecuteHealthChecks(IServiceProvider services, EnvironmentCheckResults results,
        CancellationToken token)
    {
        var healthChecks = services.GetServices<IHealthCheck>().ToArray();
        if (healthChecks.Length == 0) return;

        foreach (var check in healthChecks)
        {
            var name = check.GetType().Name;
            try
            {
                var context = new HealthCheckContext
                {
                    Registration = new HealthCheckRegistration(name, check, null, null)
                };
                var result = await check.CheckHealthAsync(context, token);

                switch (result.Status)
                {
                    case HealthStatus.Healthy:
                        results.RegisterSuccess($"HealthCheck: {name}");
                        break;

                    case HealthStatus.Degraded:
                        results.RegisterSuccess($"HealthCheck: {name} (degraded: {result.Description})");
                        break;

                    case HealthStatus.Unhealthy:
                        var exception = result.Exception
                                        ?? new Exception(result.Description ?? $"Health check '{name}' reported unhealthy");
                        results.RegisterFailure($"HealthCheck: {name}", exception);
                        break;
                }
            }
            catch (Exception e)
            {
                results.RegisterFailure($"HealthCheck: {name}", e);
            }
        }
    }
}