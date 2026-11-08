# Command Line Tooling

JasperFx includes a lightweight CLI framework for building commands that integrate with `Microsoft.Extensions.Hosting`.

## Enabling the CLI

Wire up the JasperFx command line by calling `ApplyJasperFxExtensions` on your host builder:

<!-- snippet: sample_apply_jasperfx_extensions -->
<a id='snippet-sample_apply_jasperfx_extensions'></a>
```cs
await Host
    .CreateDefaultBuilder()
    .ApplyJasperFxExtensions()
    .RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/CliSetupSamples.cs#L12-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_apply_jasperfx_extensions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_apply_jasperfx_extensions'></a>
```cs
await Host
    .CreateDefaultBuilder()
    .ApplyJasperFxExtensions()
    .RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/CliSetupSamples.cs#L12-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_apply_jasperfx_extensions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Alternatively, use `RunJasperFxCommands` for more control over host configuration:

<!-- snippet: sample_run_jasperfx_commands -->
<a id='snippet-sample_run_jasperfx_commands'></a>
```cs
var builder = Host.CreateDefaultBuilder();

builder.ConfigureServices(services =>
{
    // Register your services here
});

await builder
    .ApplyJasperFxExtensions()
    .RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/CliSetupSamples.cs#L24-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_run_jasperfx_commands' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_run_jasperfx_commands'></a>
```cs
var builder = Host.CreateDefaultBuilder();

builder.ConfigureServices(services =>
{
    // Register your services here
});

await builder
    .ApplyJasperFxExtensions()
    .RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/CliSetupSamples.cs#L24-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_run_jasperfx_commands' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Built-in Commands

JasperFx ships with several commands out of the box:

| Command | Description |
|---------|-------------|
| `help` | List all available commands |
| `describe` | Describe the application configuration |
| `check-env` | Run all registered environment checks |
| `resources` | Set up, check, clear, tear down, or list the application's [stateful resources](./resources) |

## Command Discovery

Commands are discovered automatically from referenced assemblies that carry the `[JasperFxTool]` attribute. Your own commands are found through assembly scanning.

## Machine-readable command catalog

`help --json` writes the command catalog to stdout as JSON — each verb's `name` and `description` —
for tooling that needs to introspect an app's commands:

```bash
dotnet run -- help --json
```

```json
[
  { "name": "check-env", "description": "Execute all environment checks against the application" },
  { "name": "describe", "description": "Writes out a description of your running application ..." }
]
```

Like `help` itself, this runs without starting the host (no database/broker connections), so it is
cheap to call. The [Aspire dashboard integration](/cli/aspire#dynamic-command-discovery) uses it to
discover a service's verbs.

## Native AOT

The command line runs from a Native AOT binary as well as under `dotnet run`. `help`, `describe`,
`check-env`, `resources`, `projections`, `run` and your own commands all work from the published
binary, as long as every assembly that contributes commands is built with `JasperFx.SourceGenerator`.
The generator registers each command, its input and its `[InjectService]` properties as ordinary code,
so nothing is found by reflection at run time. JasperFx and JasperFx.Events ship built with it; add it
to your own application for your own commands:

```xml
<ItemGroup>
    <PackageReference Include="JasperFx.SourceGenerator" Version="$(JasperFxVersion)"
                      PrivateAssets="all" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

`codegen write` still needs Roslyn, so run it with `dotnet run` before you publish.
`ApplyJasperFxExtensions` and `RunJasperFxCommands` keep their trimming warnings for assemblies built
without the generator; see [Running the command line in an AOT binary](/codegen/aot#running-the-command-line-in-an-aot-binary)
for the `Main` that suppresses them and the rest of the details. Since JasperFx 2.101.0.

## Topics

- [Writing Commands](/cli/writing-commands) -- Create synchronous and async commands
- [Arguments and Flags](/cli/arguments-flags) -- Define inputs with attributes
- [Environment Checks](/cli/environment-checks) -- Validate runtime dependencies
- [Describe](/cli/describe) -- Customize the describe output
- [Publishing AOT](/codegen/aot) -- Native AOT publishing, including the command line
