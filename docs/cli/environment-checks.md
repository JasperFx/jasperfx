# Environment Checks

Environment checks let you verify that external dependencies are available when your application starts.

## Registering Checks

Use the `CheckEnvironment` extension methods on `IServiceCollection`:

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

## Typed Service Checks

You can resolve a registered service and check it directly:

<!-- snippet: sample_environment_check_with_service -->
<a id='snippet-sample_environment_check_with_service'></a>
```cs
public static void RegisterTypedCheck(IServiceCollection services)
{
    services.CheckEnvironment<IConfiguration>(
        "Required config keys present",
        config =>
        {
            if (config?.GetValue<string>("ConnectionString") is null)
            {
                throw new Exception("ConnectionString is required");
            }
        });
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EnvironmentCheckSamples.cs#L33-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_environment_check_with_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_environment_check_with_service'></a>
```cs
public static void RegisterTypedCheck(IServiceCollection services)
{
    services.CheckEnvironment<IConfiguration>(
        "Required config keys present",
        config =>
        {
            if (config?.GetValue<string>("ConnectionString") is null)
            {
                throw new Exception("ConnectionString is required");
            }
        });
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/EnvironmentCheckSamples.cs#L33-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_environment_check_with_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Running Checks

Run all registered checks from the command line:

```bash
dotnet run -- check-env
```

Each check runs and reports success or failure. The command exits with a non-zero code if any check fails.

## Inline Registration via JasperFxOptions

You can also register checks directly through `JasperFxOptions`:

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

## Required Files

A common check is verifying that a configuration file exists. JasperFx provides a shorthand:

```csharp
services.AddJasperFx(opts =>
{
    opts.RequireFile("appsettings.json");
    opts.RequireFile("certs/server.pfx");
});
```

## Best Practices

- Keep checks fast. They run at startup and slow checks delay your application.
- Throw descriptive exceptions so failures are easy to diagnose.
- Use environment checks for external dependencies (databases, files, services) rather than internal validation.

## Next Steps

- [Describe](/cli/describe) -- Customize application description output
- [Configuration](/configuration/jasperfx-options) -- Full options reference
