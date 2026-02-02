# JasperFx - Claude Code Reference

## Project Overview

JasperFx is the foundational framework for the "Critter Stack" - a collection of .NET tools including Marten and Wolverine. It provides:

- **Core utilities** - Extension methods, reflection helpers, caching primitives
- **Command-line framework** - CLI parsing and command discovery (formerly Oakton)
- **Runtime code generation** - Roslyn-based dynamic C# generation and compilation
- **Event store abstractions** - Projection patterns and async daemon for event sourcing
- **Multi-tenancy support** - Tenant isolation patterns

## Tech Stack

- **Language:** C# 13, targeting .NET 8.0/9.0/10.0
- **Build:** Nuke (C# build automation)
- **Testing:** xUnit + Shouldly + NSubstitute
- **Key Dependencies:**
  - Microsoft.CodeAnalysis (Roslyn) - Runtime compilation
  - Spectre.Console - CLI output formatting
  - Polly.Core - Resilience/retry policies
  - FastExpressionCompiler - Performance optimization

## Directory Structure

```
src/
├── JasperFx/                    # Core library
│   ├── Core/                    # Utilities, extensions, reflection
│   │   ├── TypeScanning/        # Assembly and type discovery
│   │   ├── Reflection/          # Reflection helpers
│   │   ├── Filters/             # Composable type filters
│   │   └── IoC/                 # DI abstractions
│   ├── CodeGeneration/          # Runtime code generation
│   │   ├── Model/               # GeneratedType, GeneratedMethod, Variable
│   │   ├── Frames/              # Code frame abstractions
│   │   └── Services/            # Service/DI code generation
│   ├── CommandLine/             # CLI framework
│   │   ├── Commands/            # Built-in commands
│   │   └── Descriptions/        # Help generation
│   ├── MultiTenancy/            # Tenant support
│   └── Descriptors/             # Configuration metadata
│
├── JasperFx.Events/             # Event sourcing abstractions
│   ├── Projections/             # Projection definitions
│   ├── Daemon/                  # Async projection daemon
│   ├── Aggregation/             # Event aggregation
│   ├── Grouping/                # Event grouping/slicing
│   └── Subscriptions/           # Event subscriptions
│
├── JasperFx.RuntimeCompiler/    # Roslyn compilation wrapper
│
├── CoreTests/                   # Tests for Core
├── CodegenTests/                # Tests for CodeGeneration
├── CommandLineTests/            # Tests for CLI framework
├── EventTests/                  # Tests for Events
│
└── TestHarnesses/               # Integration test projects
    ├── CommandLineRunner/
    ├── GeneratorTarget/
    └── WebServiceTarget/
```

## Build Commands

```bash
# Full build and test (default)
./build.sh

# Individual test suites
./build.sh test-core
./build.sh test-codegen
./build.sh test-command-line
./build.sh test-events

# All tests
./build.sh test

# Package for NuGet
./build.sh nuget-pack

# Clean
./build.sh clean
```

Build configuration: `build/Build.cs`

## Key Entry Points

| Concern | Start Here |
|---------|------------|
| CLI commands | `src/JasperFx/CommandLine/IJasperFxCommand.cs:9` |
| Code generation | `src/JasperFx/CodeGeneration/Model/GeneratedType.cs:15` |
| Projections | `src/JasperFx.Events/Projections/ProjectionBase.cs:10` |
| Type scanning | `src/JasperFx/Core/TypeScanning/TypeRepository.cs` |
| Extensions | `src/JasperFx/Core/StringExtensions.cs` |

## NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| JasperFx | 1.17.0 | Core utilities, CLI, code generation |
| JasperFx.Events | 1.17.0 | Event sourcing abstractions |
| JasperFx.RuntimeCompiler | 4.3.2 | Roslyn compilation |

## Testing Conventions

- Tests use xUnit with Shouldly assertions
- Test projects mirror source structure (e.g., `CoreTests/` tests `JasperFx/Core/`)
- Test classes typically named `{ClassName}Tests`
- Use `NSubstitute` for mocking interfaces
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
