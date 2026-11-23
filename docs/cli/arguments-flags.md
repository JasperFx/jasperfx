# Arguments and Flags

Command inputs are plain C# classes whose properties are mapped to arguments and flags.

## Defining an Input Class

<!-- snippet: sample_build_input -->
<a id='snippet-sample_build_input'></a>
```cs
public class BuildInput
{
    [Description("The target configuration")]
    public string Configuration { get; set; } = "Debug";

    [Description("The output directory")]
    public string OutputPath { get; set; } = "./bin";

    [FlagAlias("verbose", 'v')]
    [Description("Enable verbose output")]
    public bool VerboseFlag { get; set; }

    [FlagAlias("force", 'f')]
    [Description("Force a clean rebuild")]
    public bool ForceFlag { get; set; }

    [Description("Maximum degree of parallelism")]
    public int ParallelCount { get; set; } = 4;
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/ArgumentFlagSamples.cs#L5-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_build_input' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
<a id='snippet-sample_build_input'></a>
```cs
public class BuildInput
{
    [Description("The target configuration")]
    public string Configuration { get; set; } = "Debug";

    [Description("The output directory")]
    public string OutputPath { get; set; } = "./bin";

    [FlagAlias("verbose", 'v')]
    [Description("Enable verbose output")]
    public bool VerboseFlag { get; set; }

    [FlagAlias("force", 'f')]
    [Description("Force a clean rebuild")]
    public bool ForceFlag { get; set; }

    [Description("Maximum degree of parallelism")]
    public int ParallelCount { get; set; } = 4;
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/ArgumentFlagSamples.cs#L5-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_build_input' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Arguments

Public properties that are not suffixed with `Flag` are treated as positional arguments. They are matched in the order they appear on the class.

- **Required arguments** -- Non-nullable properties without a default value
- **Optional arguments** -- Properties with a default value

## Flags

Properties whose names end with `Flag` are treated as command line flags (options).

### Flag Aliases

Use `[FlagAlias]` to define short and long flag names:

```csharp
[FlagAlias("verbose", 'v')]
public bool VerboseFlag { get; set; }
```

This allows both `--verbose` and `-v` on the command line.

### Flag Types

| .NET Type | CLI Syntax | Example |
|-----------|-----------|---------|
| `bool` | `--flag` | `--verbose` |
| `string` | `--flag value` | `--output ./bin` |
| `int` | `--flag value` | `--parallel 8` |
| `Enum` | `--flag value` | `--level Debug` |

Numbers and `TimeSpan` quantities are read with the invariant culture, so the decimal separator is always `.`,
whatever culture the machine runs in. `--ratio 1.5` means one and a half on a German machine too, where it used to
read as 15. Dates are the exception: a `DateTime` value is still parsed with the machine's culture, because a local
date format is a fair thing to type. Command and flag names are matched the same way in every culture as well.

## Flags Every Command Gets

A command whose input derives from `NetCoreInput`, which is every command that builds your host, also takes these:

| Flag | What it does |
|---|---|
| `--environment <name>` | Overrides `IHostEnvironment.EnvironmentName` |
| `--contentRoot <path>` | Overrides `IHostEnvironment.ContentRoot` |
| `--applicationName <name>` | Overrides `IHostEnvironment.ApplicationName` |
| `--config:<Key> <value>` | Overrides one configuration item, for example `--config:Database:Port 5433` |
| `--env-variable` / `-v` `KEY=VALUE` | Sets an environment variable for this process |
| `--log-level <level>` | Overrides the log level |
| `--verbose` | Writes much more at startup and turns on console logging |

`--log-level` and `--verbose` win over any `Logging:LogLevel` rules your configuration binds. Before 3.1 a
configured rule silently overrode them.

## Description Attribute

Use `[Description]` on properties to provide help text displayed in the CLI:

```csharp
[Description("The target configuration")]
public string Configuration { get; set; } = "Debug";
```

## Next Steps

- [Writing Commands](/cli/writing-commands) -- Build commands using input classes
- [Environment Checks](/cli/environment-checks) -- Startup validation
