# Critter Stack Defaults

JasperFx provides a set of opinionated defaults for applications built on the Critter Stack (Marten, Wolverine, and related libraries).

## Applying Defaults

Use `AddJasperFx` on `IServiceCollection` to register JasperFx services with configuration:

<!-- snippet: sample_critter_stack_defaults -->
<a id='snippet-sample_critter_stack_defaults'></a>
```cs
public static IHostBuilder ConfigureWithDefaults()
{
    return Host.CreateDefaultBuilder()
        .ConfigureServices(services =>
        {
            services.AddJasperFx(opts =>
            {
                // Require a file to exist at application startup
                opts.RequireFile("appsettings.json");

                // Set the development environment name if non-standard
                opts.DevelopmentEnvironmentName = "Local";
            });
        });
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/ConfigurationSamples.cs#L9-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_critter_stack_defaults' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## What Gets Registered

Calling `AddJasperFx` registers:

- `JasperFxOptions` as a singleton
- Environment check infrastructure
- System part discovery for the `describe` command
- Assembly scanning for command discovery

## Profiles

JasperFx supports named profiles to vary behavior by environment:

| Profile | Typical Use |
|---------|-------------|
| `Development` | Local development with verbose logging |
| `Staging` | Pre-production validation |
| `Production` | Optimized for reliability |

Set the active profile in configuration:

```csharp
services.AddJasperFx(opts =>
{
    opts.ActiveProfile = "Production";
});
```

## Required Files

Register files that must exist for the application to start:

```csharp
services.AddJasperFx(opts =>
{
    opts.RequireFile("appsettings.json");
    opts.RequireFile("keys/signing.key");
});
```

These are automatically verified by the `check-env` command.

## Next Steps

- [JasperFx Options](/configuration/jasperfx-options) -- Detailed options reference
- [Environment Checks](/cli/environment-checks) -- Runtime dependency validation
