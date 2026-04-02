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
