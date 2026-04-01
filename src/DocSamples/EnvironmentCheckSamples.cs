using JasperFx.Environment;
using Microsoft.Extensions.DependencyInjection;

namespace DocSamples;

public class EnvironmentCheckSamples
{
    #region sample_register_environment_check
    public static void RegisterChecks(IServiceCollection services)
    {
        // Async check with IServiceProvider access
        services.CheckEnvironment(
            "Database is reachable",
            async (IServiceProvider sp, CancellationToken ct) =>
            {
                // Throw an exception to indicate failure
                await Task.CompletedTask;
            });

        // Synchronous check
        services.CheckEnvironment(
            "Configuration file exists",
            (IServiceProvider sp) =>
            {
                if (!File.Exists("appsettings.json"))
                {
                    throw new FileNotFoundException("Missing configuration file");
                }
            });
    }
    #endregion

    #region sample_environment_check_with_service
    public static void RegisterTypedCheck(IServiceCollection services)
    {
        services.CheckEnvironment<IConfiguration>(
            "Required config keys present",
            config =>
            {
                if (config?.GetValue<string>("ConnectionString") is null)
                {
                    throw new Exception("ConnectionString is required");
                }
            });
    }
    #endregion
}

// Placeholder for sample compilation
public interface IConfiguration
{
    T? GetValue<T>(string key);
}
