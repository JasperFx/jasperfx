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

## Description Attribute

Use `[Description]` on properties to provide help text displayed in the CLI:

```csharp
[Description("The target configuration")]
public string Configuration { get; set; } = "Debug";
```

## Next Steps

- [Writing Commands](/cli/writing-commands) -- Build commands using input classes
- [Environment Checks](/cli/environment-checks) -- Startup validation
