using JasperFx.CommandLine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JasperFx.Events.CommandLine;

[Description("Asynchronous projection and projection rebuilds")]
public class ProjectionsCommand: JasperFxAsyncCommand<ProjectionInput>
{
    public ProjectionsCommand()
    {
        Usage("Just run the projections!");
        Usage("Run a specific action").Arguments(x => x.Action);
    }

    public override async Task<bool> Execute(ProjectionInput input)
    {
        if (input.HostBuilder != null)
        {
            input.HostBuilder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            });
        }
        
        using var host = input.BuildHost();

        var controller = new ProjectionController(new ProjectionHost(host), new ConsoleView());

        return await controller.Execute(input).ConfigureAwait(false);
    }
}