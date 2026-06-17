# JasperFx Options

`JasperFxOptions` is the central configuration class for JasperFx. It extends `SystemPartBase` and participates in the describe command output.

## Registering Options

<!-- snippet: sample_add_jasperfx_options -->
<a id='snippet-sample_add_jasperfx_options'></a>
```cs
public static void ConfigureOptions(IServiceCollection services)
{
    services.AddJasperFx(opts =>
    {
        // Register an environment check inline
        opts.RegisterEnvironmentCheck(
            "Database connectivity",
            async (sp, ct) =>
            {
                // Verify your database is accessible
                await Task.CompletedTask;
            });
    });
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/ConfigurationSamples.cs#L27-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_add_jasperfx_options' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Key Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ActiveProfile` | `string` | `"Development"` | The current application profile |
| `RequiredFiles` | `string[]` | `[]` | Files that must exist at startup |
| `RememberedApplicationAssembly` | `Assembly?` | `null` | Override the detected application assembly |

## Profiles

Settings that should differ between development and production live on a `Profile`. `JasperFxOptions`
exposes a `Development` and a `Production` profile, and selects one as the `ActiveProfile` at startup
based on the hosting environment (`Development` when `IHostEnvironment.IsDevelopment()`, otherwise
`Production`).

```csharp
services.AddJasperFx(opts =>
{
    // Fail fast locally so developers see migration problems immediately (this is the default)
    opts.Development.ResourceMigrationFailureMode = ResourceMigrationFailureMode.FailFast;

    // In production, don't let a transient startup migration failure crash the process
    opts.Production.ResourceMigrationFailureMode = ResourceMigrationFailureMode.ContinueOnFailures;
});
```

### ResourceMigrationFailureMode

Controls what happens when a resource (database schema/migration, message broker objects, etc.) fails to
set up or migrate during application startup through the resource setup hosted service
(`AddResourceSetupOnStartup`, or `ApplyAllDatabaseChangesOnStartup` in the Critter Stack tools).

| Value | Behavior |
|-------|----------|
| `FailFast` (default) | A startup resource/migration failure throws and aborts application startup. |
| `ContinueOnFailures` | Startup resource/migration failures are logged but do not stop the application from starting. |

`ContinueOnFailures` is intended for production multi-replica deployments. During a rolling deploy,
several replicas can race for the migration advisory lock; a replica that loses the race would otherwise
fail with "Unable to attain a global lock in time order to apply database changes" and crash-loop, even
though the winning replica's committed migration has already made the schema current. With
`ContinueOnFailures` that replica logs the failure and starts up against the now-current schema instead
of crash-looping.

## RequireFile

Tell JasperFx that a file path is required. This adds a file-exists check to environment tests:

```csharp
opts.RequireFile("appsettings.json");
```

## RegisterEnvironmentCheck

Register an inline environment check:

```csharp
opts.RegisterEnvironmentCheck(
    "Redis is reachable",
    async (sp, ct) =>
    {
        // throw on failure
    });
```

## Application Assembly Detection

JasperFx automatically detects the main application assembly for scanning. In test scenarios, you can override this:

```csharp
JasperFxOptions.RememberedApplicationAssembly = typeof(MyApp).Assembly;
```

This is useful when test runners or IDEs change the entry assembly.

## Next Steps

- [Critter Stack Defaults](/configuration/critter-stack-defaults) -- Opinionated setup
- [Environment Checks](/cli/environment-checks) -- Startup validation
