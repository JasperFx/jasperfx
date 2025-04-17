using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace JasperFx.CommandLine.Descriptions;

internal class ConfigurationPreview : SystemPartBase
{
    private const string PreviewErrorMessage = "Unable to show a preview of the configuration.";
    private readonly IConfiguration _configuration;

    public ConfigurationPreview(IConfiguration configuration) : base("IConfiguration Preview")
    {
        _configuration = configuration;
    }

    public override Task WriteToConsole()
    {
        if (!(_configuration is IConfigurationRoot root))
        {
            AnsiConsole.MarkupLine($"[red]{PreviewErrorMessage}[/]");
            return Task.CompletedTask;
        }

        void RecurseChildren(IHasTreeNodes node, IEnumerable<IConfigurationSection> children)
        {
            foreach (var child in children)
            {
                var valuesAndProviders = GetValueAndProviders(root, child.Path);

                var parent = node;
                if (valuesAndProviders.Count == 0)
                {
                    parent = node.AddNode($"[blue]{child.Key.EscapeMarkup()}[/]");
                }
                else
                {
                    // Remove the last value added to the stack. This is the "current" value
                    var finalValue = valuesAndProviders.Pop();
                    var currentNode = node.AddNode(
                        new Table()
                            .Border(TableBorder.None)
                            .HideHeaders()
                            .AddColumn("Key")
                            .AddColumn("Value")
                            .AddColumn("Provider")
                            .HideHeaders()
                            .AddRow($"[yellow]{child.Key.EscapeMarkup()}[/]", finalValue.Value.EscapeMarkup(),
                                $@"([grey]{finalValue.Provider.ToString().EscapeMarkup()}[/])")
                    );

                    // Loop through the remaining (overridden) values
                    // Display them as children of the current value
                    foreach (var overriddenValue in valuesAndProviders)
                    {
                        currentNode.AddNode(
                            new Table()
                                .Border(TableBorder.None)
                                .HideHeaders()
                                .AddColumn("Value")
                                .AddColumn("Provider")
                                .HideHeaders()
                                .AddRow($"[strikethrough]{overriddenValue.Value.EscapeMarkup()}[/]",
                                    $@"([grey]{overriddenValue.Provider.ToString().EscapeMarkup()}[/])")
                        );
                    }
                }

                RecurseChildren(parent, child.GetChildren());
            }
        }

        var tree = new Tree(string.Empty);

        RecurseChildren(tree, root.GetChildren());

        AnsiConsole.Write(tree);

        return Task.CompletedTask;
    }

    private static Stack<(string Value, IConfigurationProvider Provider)> GetValueAndProviders(
        IConfigurationRoot root,
        string key)
    {
        // Return matching values from all providers, not just the final val
        var stack = new Stack<(string, IConfigurationProvider)>();
        foreach (var provider in root.Providers)
        {
            if (provider.TryGet(key, out var value))
            {
                stack.Push((value!, provider));
            }
        }

        return stack;
    }
}