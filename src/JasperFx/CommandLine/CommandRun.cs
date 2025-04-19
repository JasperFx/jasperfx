namespace JasperFx.CommandLine;

public class CommandRun
{
    public required IJasperFxCommand Command { get; set; }
    public required object Input { get; set; }

    public Task<bool> Execute()
    {
        return Command.Execute(Input);
    }
}