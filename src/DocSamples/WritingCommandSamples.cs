using JasperFx.CommandLine;

namespace DocSamples;

#region sample_greeting_input
public class GreetingInput
{
    [Description("The name to greet")]
    public string Name { get; set; } = "World";
}
#endregion

#region sample_greeting_command
[Description("Say hello to someone")]
public class GreetingCommand : JasperFxCommand<GreetingInput>
{
    public override bool Execute(GreetingInput input)
    {
        Console.WriteLine($"Hello, {input.Name}!");
        return true;
    }
}
#endregion

#region sample_async_greeting_command
[Description("Say hello to someone asynchronously")]
public class AsyncGreetingCommand : JasperFxAsyncCommand<GreetingInput>
{
    public override async Task<bool> Execute(GreetingInput input)
    {
        await Task.Delay(100);
        Console.WriteLine($"Hello, {input.Name}!");
        return true;
    }
}
#endregion
