using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocSamples;

public class ConfigurationSamples
{
    #region sample_critter_stack_defaults
    public static IHostBuilder ConfigureWithDefaults()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddJasperFx(opts =>
                {
                    // Require a file to exist at application startup
                    opts.RequireFile("appsettings.json");

                    // Set the development environment name if non-standard
                    opts.DevelopmentEnvironmentName = "Local";
                });
            });
    }
    #endregion

    #region sample_add_jasperfx_options
    public static void ConfigureOptions(IServiceCollection services)
    {
        services.AddJasperFx(opts =>
        {
            // Register an environment check inline
            opts.RegisterEnvironmentCheck(
                "Database connectivity",
                async (sp, ct) =>
                {
                    // Verify your database is accessible
                    await Task.CompletedTask;
                });
        });
    }
    #endregion
}
