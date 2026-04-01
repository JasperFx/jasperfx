using JasperFx.CommandLine.Descriptions;
using Spectre.Console;

namespace DocSamples;

#region sample_custom_system_part
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
#endregion
