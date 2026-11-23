# Installation

## NuGet Package

Install the main JasperFx package from NuGet:

```bash
dotnet add package JasperFx
```

This brings in the core library including command line tooling, environment checks, and configuration support.

## The Other Packages

You'll usually get these through Marten, Wolverine, Polecat or Fisher rather than adding them yourself, but here's what each one is for:

| Package | What it's for |
|---|---|
| `JasperFx` | Core utilities and extension methods, the command line framework, environment checks, `describe`, and the code generation model |
| `JasperFx.RuntimeCompiler` | Compiles generated code at runtime with Roslyn. Register it with `services.AddRuntimeCompilation()`. An application that only loads pre-generated code (`TypeLoadMode.Static`) doesn't need it, and leaving it out keeps Roslyn out of an AOT publish |
| `JasperFx.Events` | The event sourcing abstractions, projections and the async daemon that Marten, Polecat and Fisher build on. Its source generator and analyzers ship inside the package |
| `JasperFx.Events.InMemory` | An in-memory document and event store for prototyping before you choose a real store. It refuses to start in Production |
| `JasperFx.Events.MicrosoftExtensionsAI` | Adapts a Microsoft.Extensions.AI embedding generator for [vector and hybrid search](/search/) |
| `JasperFx.Aspire` | Puts the command line verbs on your resource in the Aspire dashboard. See [Aspire Dashboard Integration](/cli/aspire) |
| `JasperFx.Events.ComplianceTests` | The behavioral test suites for event store authors. Ships as source |

The old `JasperFx.Core` package stopped at 2.0. Its extension methods live in `JasperFx` itself now, so reference that instead.

## Prerequisites

- .NET 10.0 SDK or later. The packages target `net10.0` and `net11.0`
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
