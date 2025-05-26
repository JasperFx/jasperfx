using System.Reflection;
using JasperFx.CommandLine.TextualDisplays;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Rendering;
using Table = Spectre.Console.Table;
using TableColumn = Spectre.Console.TableColumn;

namespace JasperFx.CommandLine.Descriptions;

[Description("Writes out a description of your running application to either the console or a file")]
public class DescribeCommand : JasperFxAsyncCommand<DescribeInput>
{
    public override async Task<bool> Execute(DescribeInput input)
    {
        using var host = input.BuildHost();

        var config = host.Services.GetRequiredService<IConfiguration>();
        var configurationPreview = new ConfigurationPreview(config);
        
        var hosting = host.Services.GetService<IHostEnvironment>();
        var about = new AboutThisAppPart(hosting);

        var builtInDescribers = new ISystemPart[] { about, configurationPreview, new ReferencedAssemblies() };


        var parts =   builtInDescribers
            .Concat(host.Services.GetServices<ISystemPart>())
            .ToArray();

        foreach (var partWithServices in parts.OfType<IRequiresServices>())
        {
            partWithServices.Resolve(host.Services);
        }

        if (input.ListFlag)
        {
            Console.WriteLine("The registered system parts are");
            foreach (var part in parts) Console.WriteLine("* " + part.Title);

            return true;
        }

        if (input.TitleFlag.IsNotEmpty())
        {
            parts = parts.Where(x => x.Title == input.TitleFlag).ToArray();
        }
        else if (input.InteractiveFlag)
        {
            var prompt = new MultiSelectionPrompt<string>()
                .Title("What part(s) of your application do you wish to view?")
                .PageSize(10)
                .AddChoices(parts.Select(x => x.Title));

            var titles = AnsiConsole.Prompt(prompt);

            parts = parts.Where(x => titles.Contains(x.Title)).ToArray();
        }

        if (input.FileFlag.IsNotEmpty())
        {
            AnsiConsole.Record();
        }

        await WriteToConsole(parts);

        if (input.FileFlag.IsNotEmpty())
        {
            await using var stream = new FileStream(input.FileFlag, FileMode.Create, FileAccess.Write);
            var writer = new StreamWriter(stream);

            if (Path.GetExtension(input.FileFlag).Contains("html", StringComparison.InvariantCulture))
            {
                await writer.WriteLineAsync(AnsiConsole.ExportHtml());
            }
            else
            {
                await writer.WriteLineAsync(AnsiConsole.ExportText());
            }
            
            await writer.FlushAsync();
            
            Console.WriteLine("Wrote system description to file " + input.FileFlag);
        }

        return true;
    }
    
    public static async Task WriteToConsole(ISystemPart[] parts)
    {
        foreach (var part in parts)
        {
            var rule = new Rule($"[blue]{part.Title}[/]")
            {
                Justification = Justify.Left
            };

            AnsiConsole.Write(rule);

            await part.WriteToConsole();

            Console.WriteLine();
            Console.WriteLine();
        }
    }
}

public class AboutThisAppPart : SystemPartBase
{
    private readonly IHostEnvironment _host;

    public AboutThisAppPart(IHostEnvironment host) : base("About " + Assembly.GetEntryAssembly()?.GetName().Name ?? "This Application", new Uri("system://environment"))
    {
        _host = host;
    }

    public override Task WriteToConsole()
    {
        var table = new Table
        {
            Border = new NoTableBorder(),
            ShowHeaders = false
        };

        table.AddColumn(new TableColumn("Name").RightAligned());
        table.AddColumn(new TableColumn("Value"));

        var entryAssembly = Assembly.GetEntryAssembly();
        table.AddRow("Entry Assembly: ", entryAssembly.GetName().Name);
        table.AddRow("Version: ", entryAssembly.GetName().Version.ToString());
        table.AddRow("Application Name: ", _host.ApplicationName);
        table.AddRow("Environment: ",_host.EnvironmentName);
        table.AddRow("Content Root Path: ", _host.ContentRootPath);
        table.AddRow("AppContext.BaseDirectory: ", AppContext.BaseDirectory);

        AnsiConsole.Write(table);
        
        return Task.CompletedTask;
    }
}

public class ReferencedAssemblies : SystemPartBase
{
    public ReferencedAssemblies() : base("Referenced Assemblies", new Uri("system://assemblies"))
    {
    }
    
    public override Task WriteToConsole()
    {
        var description = new TextualDisplay("Referenced Assemblies");
        var table = description.AddTable();
        table.AddColumn("Assembly Name", textAlign:Justify.Left);
        table.AddColumn("Version");

        var referenced = Assembly.GetEntryAssembly()!.GetReferencedAssemblies();
        foreach (var assemblyName in referenced) table.AddRow(assemblyName.Name!, assemblyName.Version!.ToString());

        description.WriteToConsole();

        return Task.CompletedTask;
    }
}
