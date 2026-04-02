using JasperFx;
using Microsoft.Extensions.Hosting;

namespace DocSamples;

public class CliSetupSamples
{
    public async Task setup_jasperfx_cli()
    {
        var args = Array.Empty<string>();

        #region sample_apply_jasperfx_extensions
        await Host
            .CreateDefaultBuilder()
            .ApplyJasperFxExtensions()
            .RunJasperFxCommands(args);
        #endregion
    }

    public async Task run_jasperfx_commands()
    {
        var args = Array.Empty<string>();

        #region sample_run_jasperfx_commands
        var builder = Host.CreateDefaultBuilder();

        builder.ConfigureServices(services =>
        {
            // Register your services here
        });

        await builder
            .ApplyJasperFxExtensions()
            .RunJasperFxCommands(args);
        #endregion
    }
}
