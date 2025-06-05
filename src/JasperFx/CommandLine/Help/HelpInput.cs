namespace JasperFx.CommandLine.Help;

public class HelpInput
{
    [IgnoreOnCommandLine] public IEnumerable<Type> CommandTypes { get; set; } = null!;

    [Description("A command name")] public string Name { get; set; } = null!;

    [IgnoreOnCommandLine] public bool InvalidCommandName { get; set; }

    [IgnoreOnCommandLine] public UsageGraph Usage { get; set; } = null!;

    [IgnoreOnCommandLine] public string AppName { get; set; } = "dotnet run --";
}