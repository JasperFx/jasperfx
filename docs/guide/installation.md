# Installation

## NuGet Package

Install the main JasperFx package from NuGet:

```bash
dotnet add package JasperFx
```

This brings in the core library including command line tooling, environment checks, and configuration support.

## Core Extensions Only

If you only need the utility extension methods (string, enumerable, reflection), you can install the lighter-weight core package:

```bash
dotnet add package JasperFx.Core
```

## Prerequisites

- .NET 9.0 SDK or later
- A `Microsoft.Extensions.Hosting` compatible application (for CLI and environment check features)

## Verifying Installation

After installing, verify the package is available by adding JasperFx to your host builder:

```csharp
using Microsoft.Extensions.Hosting;

await Host
    .CreateDefaultBuilder()
    .ApplyJasperFxExtensions(args);
```

Run your application with the `help` command to see available commands:

```bash
dotnet run -- help
```

## Next Steps

- [Quick Start](/guide/quickstart) -- Wire up JasperFx in a minimal application
- [CLI Commands](/cli/) -- Learn about the command line framework
