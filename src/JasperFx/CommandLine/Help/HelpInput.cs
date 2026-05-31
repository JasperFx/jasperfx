namespace JasperFx.CommandLine.Help;

public class HelpInput
{
    [IgnoreOnCommandLine] public IEnumerable<Type> CommandTypes { get; set; } = null!;

    [Description("A command name")] public string Name { get; set; } = null!;

    [Description("Write the command catalog as JSON to stdout (machine-readable; no host startup)")]
    public bool JsonFlag { get; set; }

    [IgnoreOnCommandLine] public bool InvalidCommandName { get; set; }

    [IgnoreOnCommandLine] public UsageGraph Usage { get; set; } = null!;

    [IgnoreOnCommandLine] public string AppName { get; set; } = "dotnet run --";
}