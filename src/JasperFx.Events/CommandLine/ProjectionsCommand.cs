using JasperFx.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

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

        // jasperfx#1205: see and manage a projection's persisted start floor. No daemon involved.
        if (input.Action == ProjectionAction.floor)
        {
            if (input.AdvanceFlag)
            {
                AnsiConsole.MarkupLine("[yellow]--advance only applies to the run and rebuild actions. No high water mark was moved[/]");
            }

            var stores = host.Services.GetServices<IEventStore>().ToArray();
            return await new ProjectionFloorController(stores, new ProjectionFloorView()).ExecuteAsync(input)
                .ConfigureAwait(false);
        }

        // marten#6003: list parked identities, or write durable replay/discard requests. No daemon involved.
        if (input.Action == ProjectionAction.parked)
        {
            var stores = host.Services.GetServices<IEventStore>().ToArray();
            return await new ParkedIdentityController(stores, new ParkedIdentityView()).ExecuteAsync(input)
                .ConfigureAwait(false);
        }

        var controller = new ProjectionController(new ProjectionHost(host), new ConsoleView());

        return await controller.Execute(input).ConfigureAwait(false);
    }
}