# Quick Start

Get a JasperFx-enabled application running in under a minute.

## Minimal Setup

Create a new console application and install JasperFx:

```bash
dotnet new console -n MyApp
cd MyApp
dotnet add package JasperFx
```

Replace the contents of `Program.cs`:

<!-- snippet: sample_quickstart_minimal -->
<a id='snippet-sample_quickstart_minimal'></a>
```cs
await Host
    .CreateDefaultBuilder()
    .ApplyJasperFxExtensions()
    .RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/QuickStartSamples.cs#L12-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_minimal' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Running Commands

With this setup you immediately get access to built-in commands:

```bash
# Show all available commands
dotnet run -- help

# Describe the application configuration
dotnet run -- describe

# Run environment checks
dotnet run -- check-env
```

## Adding Custom Commands

You can register your own commands by creating classes that extend `JasperFxCommand<T>` or `JasperFxAsyncCommand<T>`. See [Writing Commands](/cli/writing-commands) for details.

## Adding Environment Checks

Register checks to verify your application's external dependencies at startup:

<!-- snippet: sample_register_environment_check -->
<a id='snippet-sample_register_environment_check'></a>
```cs
public static void RegisterChecks(IServiceCollection services)
{
    // Async check with IServiceProvider access
    services.CheckEnvironment(
        "Database is reachable",
        async (IServiceProvider sp, CancellationToken ct) =>
        {
            // Throw an exception to indicate failure
            await Task.CompletedTask;
        });

    // Synchronous check
    services.CheckEnvironment(
        "Configuration file exists",
        (IServiceProvider sp) =>
        {
            if (!File.Exists("appsettings.json"))
            {
                throw new FileNotFoundException("Missing configuration file");
            }
        });
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EnvironmentCheckSamples.cs#L8-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_environment_check' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## What Next?

- [Writing Commands](/cli/writing-commands) -- Create custom CLI commands
- [Environment Checks](/cli/environment-checks) -- Validate your runtime environment
- [Configuration](/configuration/critter-stack-defaults) -- Configure JasperFx options
