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

## IHealthCheck Integration

JasperFx automatically discovers and runs any registered `IHealthCheck` implementations as part of the `check-env` command. This bridges the standard .NET health check ecosystem with JasperFx's environment check pipeline.

### Registering Health Checks

Use the `CheckEnvironmentHealthCheck<T>()` extension method:

```csharp
services.CheckEnvironmentHealthCheck<DatabaseHealthCheck>();
services.CheckEnvironmentHealthCheck<RedisHealthCheck>();
```

Or register an instance directly:

```csharp
services.CheckEnvironmentHealthCheck(new MyCustomHealthCheck());
```

Any `IHealthCheck` implementation already registered in your DI container (for example, via `AddHealthChecks().AddCheck<T>()`) will also be picked up automatically by `check-env`.

### Health Check Result Mapping

| IHealthCheck Status | Environment Check Result |
|---------------------|--------------------------|
| `Healthy`           | Success                  |
| `Degraded`          | Success (with warning)   |
| `Unhealthy`         | Failure                  |

### Example

```csharp
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbConnection _connection;

    public DatabaseHealthCheck(IDbConnection connection)
    {
        _connection = connection;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await _connection.OpenAsync(ct);
            return HealthCheckResult.Healthy("Database connection is active");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot connect to database", ex);
        }
    }
}

// Registration
services.CheckEnvironmentHealthCheck<DatabaseHealthCheck>();
```

## Best Practices

- Keep checks fast. They run at startup and slow checks delay your application.
- Throw descriptive exceptions so failures are easy to diagnose.
- Use environment checks for external dependencies (databases, files, services) rather than internal validation.
- Prefer `IHealthCheck` for checks that should also be available via ASP.NET Core's `/health` endpoint.

## Next Steps

- [Describe](/cli/describe) -- Customize application description output
- [Configuration](/configuration/jasperfx-options) -- Full options reference
