# Stateful Resources

Most Critter Stack applications depend on *stateful resources*: database schemas, message broker
queues and exchanges, cloud storage buckets, and the like. JasperFx models each of these as an
`IStatefulResource`. Libraries like Marten, Polecat, and Wolverine expose theirs automatically, so one
command (or one line of startup configuration) can set up, check, clear, or tear down every piece of
infrastructure your application needs.

## The resources command

The built-in `resources` command runs an action against every stateful resource in the application:

```bash
# Set up every resource (setup is the default action)
dotnet run -- resources

# Same thing, spelled out
dotnet run -- resources setup
```

| Action | What it does |
|--------|--------------|
| `setup` (default) | Calls `Setup()` on each resource, then shows the result of `DetermineStatus()` |
| `check` | Calls `Check()` on each resource. A resource signals a problem by throwing |
| `clear` | Calls `ClearState()` on each resource to wipe persisted state (rows, queued messages) while keeping the resource itself |
| `teardown` | Calls `Teardown()` on each resource to remove it entirely |
| `statistics` | Shows the result of `DetermineStatus()` for each resource |
| `list` | Lists each resource's subject URI, resource URI, name, and type without touching it |

Every action except `list` shows its progress and then a tree of the results, grouped by resource type.
Any failure is written to the console and makes the command exit with a non-zero code, so
`dotnet run -- resources check` works as a deployment or CI gate.

### Flags

| Flag | Description |
|------|-------------|
| `-t`, `--type` | Only act on resources whose `Type` matches (case-insensitive) |
| `-n`, `--name` | Only act on resources whose `Name` matches (case-insensitive) |
| `--timeout` | Timeout in seconds for the whole command. The default is 60 |

```bash
# Only set up Wolverine's resources
dotnet run -- resources setup -t Wolverine

# Clear the state of a single resource
dotnet run -- resources clear --name incoming --type RabbitMQ

# Give a slow environment more time
dotnet run -- resources setup --timeout 300
```

When the timeout expires, the command stops before the next resource, prints `Timed out!`, and fails.

`resources` also accepts all the standard host flags, like `--environment` and `--config`. See
[Arguments & Flags](./arguments-flags) for those.

::: tip
Use `list` to find the exact type and name values to filter on. The type is the categorical name a
library gives its resources, and the name identifies one resource within that type.
:::

## Setting up resources at application startup

For a better "F5 experience" in development, you don't need to run the command at all. Instead, set up
every resource when the application starts:

```cs
var builder = Host.CreateApplicationBuilder();

// Run Setup() on every stateful resource at startup
builder.Services.AddResourceSetupOnStartup();
```

With `IHostBuilder`, there are equivalent extension methods, including one that only applies in the
`Development` environment:

```cs
Host.CreateDefaultBuilder()
    // Always set up resources at startup
    .UseResourceSetupOnStartup();

Host.CreateDefaultBuilder()
    // Only set up resources at startup when the environment is "Development"
    .UseResourceSetupOnStartupInDevelopment();
```

All of these take an optional `StartupAction`:

| StartupAction | Behavior |
|---------------|----------|
| `SetupOnly` (default) | Calls `Setup()` on every resource |
| `ResetState` | Calls `Setup()` and then `ClearState()` on every resource. Meant for automated testing, where each run should start from a known, empty state |

This startup setup runs in a hosted service that is registered first, so resources are ready before
your other hosted services start. By default a failure throws and stops the application from starting.
If you want failures logged instead, for example on production replicas racing for a migration lock,
see [ResourceMigrationFailureMode](../configuration/jasperfx-options#resourcemigrationfailuremode).

## Working with resources in tests

These `IHost` extension methods apply the same operations from code, which is handy in integration
test setup. Each one takes optional resource type and name filters:

```cs
// Setup() on every resource
await host.SetupResources();

// Setup(), then ClearState(), on every resource, for a clean slate between tests
await host.ResetResourceState();

// Teardown() on every resource
await host.TeardownResources();
```

`SetupResources()` and `ResetResourceState()` collect every failure and throw them together as an
`AggregateException`.

## Writing your own stateful resource

Implement `IStatefulResource` to bring your own infrastructure under the same model:

<!-- snippet: sample_IStatefulResource -->
<a id='snippet-sample_IStatefulResource'></a>
```cs
/// <summary>
///     Adapter interface used by JasperFx enabled applications to allow
///     JasperFx to setup/teardown/clear the state/check on stateful external
///     resources of the system like databases or messaging queues
/// </summary>
public interface IStatefulResource
{
    /// <summary>
    ///     Categorical type name of this resource for filtering
    /// </summary>
    string Type { get; }

    /// <summary>
    ///     Identifier for this resource
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Provides information about this resource's role within the system
    /// </summary>
    Uri SubjectUri { get; }
    
    /// <summary>
    /// Provides information about the resource itself
    /// </summary>
    Uri ResourceUri { get; }

    /// <summary>
    ///     Check whether the configuration for this resource is valid. An exception
    ///     should be thrown if the check is invalid
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task Check(CancellationToken token);

    /// <summary>
    ///     Clear any persisted state within this resource
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task ClearState(CancellationToken token);

    /// <summary>
    ///     Tear down the stateful resource represented by this implementation
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task Teardown(CancellationToken token);

    /// <summary>
    ///     Make any necessary configuration to this stateful resource
    ///     to make the system function correctly
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task Setup(CancellationToken token);

    /// <summary>
    ///     Optionally return a report of the current state of this resource
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<IRenderable> DetermineStatus(CancellationToken token);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/JasperFx/Resources/IStatefulResource.cs#L29-L96' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_IStatefulResource' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`StatefulResourceBase` provides no-op implementations of every method, so you only override the ones
your resource needs.

JasperFx discovers resources through the `FindResources()` method of each `ISystemPart` registered in
the application's container. That's the same `ISystemPart` that feeds the
[describe command](./describe). A custom resource is exposed by a system part that returns it.

### Ordering resources with dependencies

Resources run ordered by type and then name. If one resource must be set up before another (say, a
queue that needs its exchange to exist first), implement `IStatefulResourceWithDependencies`:

<!-- snippet: sample_IStatefulResourceWithDependencies -->
<a id='snippet-sample_IStatefulResourceWithDependencies'></a>
```cs
/// <summary>
/// Use to create dependencies between 
/// </summary>
public interface IStatefulResourceWithDependencies : IStatefulResource
{
    // Given all the known stateful resources in your system -- including the current resource!
    // tell JasperFx which resources are dependencies of this resource that should be setup first
    IEnumerable<IStatefulResource> FindDependencies(IReadOnlyList<IStatefulResource> others);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/JasperFx/Resources/IStatefulResource.cs#L5-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_IStatefulResourceWithDependencies' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

JasperFx sorts the resources topologically so that dependencies always run first.

### Creating resources that don't exist yet

A resource that must *create* something before it can be set up, like a database or a cloud
namespace, can implement `IResourceCreator`. During startup setup and the `SetupResources()` /
`ResetResourceState()` extension methods, `EnsureCreatedAsync()` runs on every `IResourceCreator`
before any resource's `Setup()`.
