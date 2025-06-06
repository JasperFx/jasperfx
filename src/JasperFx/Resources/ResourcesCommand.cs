using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace JasperFx.Resources;

[Description("Check, setup, or teardown stateful resources of this system")]
public class ResourcesCommand : JasperFxAsyncCommand<ResourceInput>
{
    public ResourcesCommand()
    {
        Usage("Ensure all stateful resources are set up").NoArguments();
        Usage("Execute an action against all resources").Arguments(x => x.Action);
    }

    public override async Task<bool> Execute(ResourceInput input)
    {
        AnsiConsole.Write(
            new FigletText("JasperFx"){Justification = Justify.Left});

        var cancellation = input.TokenSource.Token;
        using var host = input.BuildHost();
        var resources = await FindResources(input, host);

        if (!resources.Any())
        {
            AnsiConsole.MarkupLine("[gray]No matching resources.[/]");
            return true;
        }

        var allGood = new Markup("[green]Success.[/]");

        return input.Action switch
        {
            ResourceAction.setup => await ExecuteOnEach("Resource Setup", resources, cancellation,
                "Setting up resources...", async r =>
                {
                    await r.Setup(cancellation);
                    return await r.DetermineStatus(cancellation);
                }),

            ResourceAction.teardown => await ExecuteOnEach("Resource Teardown", resources, cancellation,
                "Tearing down resources...",
                async r =>
                {
                    await r.Teardown(cancellation);
                    return allGood;
                }),

            ResourceAction.statistics => await ExecuteOnEach("Resource Statistics", resources, cancellation,
                "Determining resource status...",
                r => r.DetermineStatus(cancellation)),

            ResourceAction.check => await ExecuteOnEach("Resource Checks", resources, cancellation,
                "Checking up on resources...",
                async r =>
                {
                    await r.Check(cancellation);
                    return allGood;
                }),

            ResourceAction.clear => await ExecuteOnEach("Clearing Resource State", resources, cancellation,
                "Clearing resources...", async r =>
                {
                    await r.ClearState(cancellation);
                    return allGood;
                }),

            ResourceAction.list => listAll(resources),

            _ => false
        };
    }

    private bool listAll(IList<IStatefulResource> statefulResources)
    {
        var resources = statefulResources.OrderBy(x => x.SubjectUri.ToString()).ThenBy(x => x.Name).ToArray();
        
        var table = new Table();
        table.AddColumns("Subject", "Resource", "Name", "Type");
        foreach (var resource in resources) table.AddRow(resource.SubjectUri.ToString(), resource.ResourceUri.ToString(), resource.Name, resource.Type);

        AnsiConsole.Write(table);

        return true;
    }

    public Task<List<IStatefulResource>> FindResources(ResourceInput input, IHost host)
    {
        return ResourceExecutor.FindResources(host.Services, input.TypeFlag, input.NameFlag);
    }

    internal async Task<bool> ExecuteOnEach(string heading, IList<IStatefulResource> resources, CancellationToken token,
        string progressTitle, Func<IStatefulResource, Task<IRenderable>> execution)
    {
        var exceptions = new List<Exception>();
        var records = new List<ResourceRecord>();

        var timedout = false;

        await AnsiConsole.Progress().StartAsync(async c =>
        {
            var task = c.AddTask($"[bold]{progressTitle}[/]", new ProgressTaskSettings
            {
                MaxValue = resources.Count
            });

            foreach (var resource in resources)
            {
                if (token.IsCancellationRequested)
                {
                    timedout = true;
                    break;
                }

                try
                {
                    var status = await execution(resource);
                    var record = new ResourceRecord { Resource = resource, Status = status };
                    records.Add(record);
                }
                catch (Exception e)
                {
                    AnsiConsole.WriteException(e);

                    var record = new ResourceRecord { Resource = resource, Status = new Markup("[red]Failed![/]") };
                    records.Add(record);

                    exceptions.Add(e);
                }
                finally
                {
                    task.Increment(1);
                }
            }
        });

        AnsiConsole.WriteLine();

        if (timedout)
        {
            AnsiConsole.MarkupLine("[bold red]Timed out![/]");
            return false;
        }

        var groups = records.GroupBy(x => x.Resource.Type);
        var tree = new Tree(heading);
        tree.Guide = TreeGuide.BoldLine;
        foreach (var group in groups)
        {
            var groupNode = tree.AddNode(group.Key);
            foreach (var record in group) groupNode.AddNode(record.Resource.Name).AddNode(record.Status);
        }

        AnsiConsole.Write(tree);


        return !exceptions.Any();
    }
    
    internal class ResourceRecord
    {
        public required IStatefulResource Resource { get; init; }
        public required IRenderable Status { get; init; }
    }
}