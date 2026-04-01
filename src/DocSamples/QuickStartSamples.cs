using JasperFx;
using Microsoft.Extensions.Hosting;

namespace DocSamples;

public class QuickStartSamples
{
    public async Task quickstart_minimal()
    {
        var args = Array.Empty<string>();

        #region sample_quickstart_minimal
        await Host
            .CreateDefaultBuilder()
            .ApplyJasperFxExtensions()
            .RunJasperFxCommands(args);
        #endregion
    }
}
