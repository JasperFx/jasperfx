# JasperFx - Claude Code Reference

## Project Overview

JasperFx is the foundational framework for the "Critter Stack" (.NET tools including Marten and Wolverine). It provides core utilities, a CLI framework (formerly Oakton), Roslyn-based runtime code generation, event sourcing abstractions with an async daemon, and multi-tenancy support.

## Tech Stack

- **Language:** C# 13, targeting .NET 8.0/9.0/10.0
- **Build:** Nuke (C# build automation)
- **Testing:** xUnit + Shouldly + NSubstitute
- **Key Dependencies:** Microsoft.CodeAnalysis (Roslyn), Spectre.Console, Polly.Core, FastExpressionCompiler

## Directory Structure

```
src/
├── JasperFx/                    # Core library (v1.17.2)
│   ├── Core/                    # Utilities, extensions, reflection, type scanning, filters, IoC
│   ├── CodeGeneration/          # Runtime code gen: Model/, Frames/, Services/
│   ├── CommandLine/             # CLI framework: Commands/, Descriptions/
│   ├── MultiTenancy/            # Tenant isolation patterns
│   └── Descriptors/             # Configuration metadata
│
├── JasperFx.Events/             # Event sourcing abstractions (v1.19.1)
│   ├── Projections/             # Projection definitions and lifecycle
│   ├── Daemon/                  # Async projection daemon
│   ├── Aggregation/             # Event aggregation patterns
│   ├── Grouping/                # Event grouping/slicing
│   └── Subscriptions/           # Event subscriptions
│
├── JasperFx.RuntimeCompiler/    # Roslyn compilation wrapper (v4.3.2)
├── JasperFx.Events.SourceGenerator/ # Source generator for aggregate projections (v1.19.1)
│
├── CoreTests/                   # Tests for JasperFx/Core
├── CodegenTests/                # Tests for JasperFx/CodeGeneration
├── CommandLineTests/            # Tests for JasperFx/CommandLine
├── EventTests/                  # Tests for JasperFx.Events
└── EventStoreTests/             # Additional event store tests
```

## Build Commands

```bash
./build.sh                  # Full build and test
./build.sh test-core        # Core tests only
./build.sh test-codegen     # Code generation tests only
./build.sh test-command-line # CLI tests only
./build.sh test-events      # Event tests only
./build.sh test             # All tests
./build.sh nuget-pack       # Package for NuGet
./build.sh clean            # Clean outputs
```

Build configuration: `build/Build.cs`

## Key Entry Points

| Concern | Start Here |
|---------|------------|
| CLI commands | `src/JasperFx/CommandLine/IJasperFxCommand.cs:5` |
| Code generation | `src/JasperFx/CodeGeneration/Model/GeneratedType.cs:15` |
| Frame abstraction | `src/JasperFx/CodeGeneration/Frames/Frame.cs:11` |
| Projections | `src/JasperFx.Events/Projections/ProjectionBase.cs:10` |
| Async daemon | `src/JasperFx.Events/Daemon/` |
| Type scanning | `src/JasperFx/Core/TypeScanning/TypeRepository.cs` |
| IoC conventions | `src/JasperFx/Core/IoC/AssemblyScanner.cs:27` |
| Service container | `src/JasperFx/ServiceContainer.cs` |
| Options | `src/JasperFx/JasperFxOptions.cs:17` |

## Testing Conventions

- Test projects mirror source: `CoreTests/` tests `JasperFx/Core/`, etc.
- Test classes named `{ClassName}Tests`
- NSubstitute for mocking, Shouldly for assertions
- Custom `SpecificationExtensions` in each test project for `ShouldHaveTheSameElementsAs()` etc.
- CI runs with `DISABLE_TEST_PARALLELIZATION=true`

## Additional Documentation

When working on specific areas, consult these specialized guides:

| Topic | File |
|-------|------|
| Architectural patterns | `.claude/docs/architectural_patterns.md` |

## Quick Reference

- **Solution:** `jasperfx.sln`
- **Global props:** `Directory.Build.props`
- **CI:** `.github/workflows/dotnet.yml`
- **License:** MIT
