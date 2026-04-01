# Describe Command

The `describe` command outputs a summary of your application's configuration and registered components. It is built in and available automatically.

## Running Describe

```bash
dotnet run -- describe
```

This prints information about all registered `ISystemPart` instances, including those added by Critter Stack libraries like Marten and Wolverine.

## Custom System Parts

Implement `ISystemPart` to add your own sections to the describe output:

<!-- snippet: sample_custom_system_part -->
<a id='snippet-sample_custom_system_part'></a>
```cs
public class MessagingSystemPart : SystemPartBase
{
    public MessagingSystemPart()
        : base("Messaging Subsystem", new Uri("system://messaging"))
    {
    }

    public override Task WriteToConsole()
    {
        AnsiConsole.MarkupLine("[bold]Transport:[/] RabbitMQ");
        AnsiConsole.MarkupLine("[bold]Queues:[/] 12 active");
        AnsiConsole.MarkupLine("[bold]Consumers:[/] 8 running");
        return Task.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/DescribeSamples.cs#L6-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_system_part' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_custom_system_part'></a>
```cs
public class MessagingSystemPart : SystemPartBase
{
    public MessagingSystemPart()
        : base("Messaging Subsystem", new Uri("system://messaging"))
    {
    }

    public override Task WriteToConsole()
    {
        AnsiConsole.MarkupLine("[bold]Transport:[/] RabbitMQ");
        AnsiConsole.MarkupLine("[bold]Queues:[/] 12 active");
        AnsiConsole.MarkupLine("[bold]Consumers:[/] 8 running");
        return Task.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/DescribeSamples.cs#L6-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_system_part' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Register your system part through `JasperFxOptions`:

```csharp
services.AddJasperFx(opts =>
{
    opts.Services.AddSingleton<ISystemPart, MessagingSystemPart>();
});
```

## IDescriptionWriter

The `IDescriptionWriter` interface provides methods for structured output:

| Method | Purpose |
|--------|---------|
| `BulletItem(string)` | Write a bullet point |
| `Header(string)` | Write a section header |
| `Write(string)` | Write plain text |

## Built-in System Parts

JasperFx itself registers a system part for `JasperFxOptions` that reports:

- Active profile (Development, Staging, Production)
- Required files
- Registered environment checks

Other Critter Stack libraries add their own parts automatically when referenced.

## Next Steps

- [Environment Checks](/cli/environment-checks) -- Validate dependencies at startup
- [JasperFx Options](/configuration/jasperfx-options) -- Full options reference
