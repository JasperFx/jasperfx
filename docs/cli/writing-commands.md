# Writing Commands

JasperFx provides two base classes for building CLI commands.

## Synchronous Commands

Extend `JasperFxCommand<T>` for commands that do not need async operations:

<!-- snippet: sample_greeting_command -->
<a id='snippet-sample_greeting_command'></a>
```cs
[Description("Say hello to someone")]
public class GreetingCommand : JasperFxCommand<GreetingInput>
{
    public override bool Execute(GreetingInput input)
    {
        Console.WriteLine($"Hello, {input.Name}!");
        return true;
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/WritingCommandSamples.cs#L13-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_greeting_command' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_greeting_command'></a>
```cs
[Description("Say hello to someone")]
public class GreetingCommand : JasperFxCommand<GreetingInput>
{
    public override bool Execute(GreetingInput input)
    {
        Console.WriteLine($"Hello, {input.Name}!");
        return true;
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/WritingCommandSamples.cs#L13-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_greeting_command' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `Execute` method returns `true` for success or `false` for failure. The exit code is set accordingly.

## Async Commands

Extend `JasperFxAsyncCommand<T>` when you need to perform async work:

<!-- snippet: sample_async_greeting_command -->
<a id='snippet-sample_async_greeting_command'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/WritingCommandSamples.cs#L25-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_async_greeting_command' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_async_greeting_command'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/WritingCommandSamples.cs#L25-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_async_greeting_command' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Input Classes

Every command takes an input class that defines its arguments and flags:

<!-- snippet: sample_greeting_input -->
<a id='snippet-sample_greeting_input'></a>
```cs
public class GreetingInput
{
    [Description("The name to greet")]
    public string Name { get; set; } = "World";
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/WritingCommandSamples.cs#L5-L11' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_greeting_input' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_greeting_input'></a>
```cs
public class GreetingInput
{
    [Description("The name to greet")]
    public string Name { get; set; } = "World";
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/WritingCommandSamples.cs#L5-L11' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_greeting_input' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Properties on the input class are automatically mapped to command line arguments. See [Arguments and Flags](/cli/arguments-flags) for the full attribute reference.

## Command Naming

By default, the command name is derived from the class name by removing the `Command` suffix and converting to kebab-case. For example, `GreetingCommand` becomes `greeting`.

## Usage Patterns

Commands can define multiple usage patterns similar to Git:

```csharp
public class MyCommand : JasperFxCommand<MyInput>
{
    public MyCommand()
    {
        Usage("Default usage").Arguments(x => x.Name);
    }

    public override bool Execute(MyInput input) => true;
}
```

## Next Steps

- [Arguments and Flags](/cli/arguments-flags) -- Detailed attribute reference
- [Environment Checks](/cli/environment-checks) -- Register startup validations
