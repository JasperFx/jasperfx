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

        var checks = await services.DiscoverChecks();
        if (!checks.Any())
        {
            AnsiConsole.WriteLine("No environment checks.");
            return results;
        }

        await AnsiConsole.Progress().StartAsync(async c =>
        {
            var task = c.AddTask("[bold]Running Environment Checks[/]", new ProgressTaskSettings
            {
                MaxValue = checks.Count
            });

            for (var i = 0; i < checks.Count; i++)
            {
                var check = checks[i];

                try
                {
                    await check.Assert(services, token);

                    AnsiConsole.MarkupLine(
                        $"[green]{(i + 1).ToString().PadLeft(4)}.) Success: {check.Description}[/]");

                    results.RegisterSuccess(check.Description);
                }
                catch (Exception e)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]{(i + 1).ToString().PadLeft(4)}.) Failed: {check.Description}[/]");
                    AnsiConsole.WriteException(e);

                    results.RegisterFailure(check.Description, e);
                }
                finally
                {
                    task.Increment(1);
                }
            }

            task.StopTask();
        });

        return results;
    }

    // TODO -- get a unit test on this
    public static async Task<IList<IEnvironmentCheck>> DiscoverChecks(this IServiceProvider services)
    {
        var list = new List<IEnvironmentCheck>();
        list.AddRange(services.GetServices<IEnvironmentCheck>());

        foreach (var factory in services.GetServices<IEnvironmentCheckFactory>())
        {
            list.AddRange(await factory.Build());
        }
        
        list.AddRange(services.GetServices<IStatefulResource>().Select(x => new ResourceEnvironmentCheck(x)));

        foreach (var source in services.GetServices<ISystemPart>())
        {
            foreach (var resource in await source.FindResources())
            {
                list.Add(new ResourceEnvironmentCheck(resource));
            }
        }

        return list;
    }
}