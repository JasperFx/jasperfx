// jasperfx#1141: the JasperFx command line inside a Native AOT binary. See the csproj for how
// build/Build.cs (smoke-test-aot-cli) publishes and drives it.

using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CommandLine;
using JasperFx.Events;

namespace JasperFx.AotCliSmoke;

public static class Program
{
    // The suppression docs/codegen/aot.md recommends. ApplyJasperFxExtensions and RunJasperFxCommands
    // keep their [RequiresUnreferencedCode] / [RequiresDynamicCode] because an attribute cannot know
    // whether JasperFx.SourceGenerator ran; here it did, over every assembly that contributes commands.
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "JasperFx.SourceGenerator registers the commands, their factories and their input parsers in this app, JasperFx and JasperFx.Events.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Every command input has a source-generated parser, so no List<T> is closed at runtime.")]
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.CritterStackDefaults(opts =>
        {
            opts.Production.GeneratedCodeMode = TypeLoadMode.Static;
            opts.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
        });

        builder.Services.AddSingleton<Greeter>();
        builder.Services.AddHostedService<StopAfterStart>();

        builder.Host.ApplyJasperFxExtensions();

        // Any Marten / Polecat / Fisher app uses JasperFx.Events, which keeps it, and the commands it
        // contributes (projections, event-query, ...), in the image. This app has no store, so it
        // touches the assembly by hand.
        _ = Event.For(new Pinged(1));

        var app = builder.Build();
        app.MapGet("/", () => "Hello World!");

        return await app.RunJasperFxCommands(args);
    }
}

public record Pinged(int Number);

public class Greeter
{
    public string Greet(string name, bool shout)
    {
        var greeting = $"Hello, {name}";
        return shout ? greeting.ToUpperInvariant() : greeting;
    }
}

public class GreetInput
{
    [Description("Who to greet")]
    public string Name { get; set; } = "";

    [Description("Greet loudly")]
    [FlagAlias("shout", 's')]
    public bool ShoutFlag { get; set; }

    // An enumerable flag: the reflective parser closes List<T> with MakeGenericType for this, which
    // a Native AOT image cannot do, so it proves the source-generated parser is the one in use.
    [Description("Tags to print after the greeting")]
    public List<string> TagsFlag { get; set; } = new();
}

[Description("Greets someone through a service injected from the application's container", Name = "greet")]
public class GreetCommand : JasperFxCommand<GreetInput>
{
    [InjectService]
    public Greeter Greeter { get; set; } = null!;

    public override bool Execute(GreetInput input)
    {
        Console.WriteLine(Greeter.Greet(input.Name, input.ShoutFlag));
        Console.WriteLine("tags: " + string.Join(",", input.TagsFlag));
        return true;
    }
}

/// <summary>
///     Lets the smoke gate prove `run` without leaving a server running: with
///     AOT_CLI_SMOKE_STOP_AFTER_START set, the host stops itself once it has started.
/// </summary>
public class StopAfterStart : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;

    public StopAfterStart(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (System.Environment.GetEnvironmentVariable("AOT_CLI_SMOKE_STOP_AFTER_START") != null)
        {
            _lifetime.ApplicationStarted.Register(() =>
            {
                Console.WriteLine("AOT CLI smoke: host started");
                _lifetime.StopApplication();
            });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
