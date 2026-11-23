# Introduction

JasperFx is the shared infrastructure library that underpins the Critter Stack family of .NET libraries, including [Marten](https://martendb.io) and [Wolverine](https://wolverinefx.io).

## What Does JasperFx Provide?

JasperFx gives you:

- **Command line tooling** -- A lightweight framework for building CLI commands with argument parsing, flag support, and automatic help generation.
- **Environment checks** -- Register checks that run at application startup to verify external dependencies are available.
- **Describe command** -- A built-in command that outputs a summary of your application's configuration and registered components.
- **Configuration** -- `JasperFxOptions` and Critter Stack defaults for consistent application setup.
- **Utility extensions** -- String, enumerable, and reflection helpers used throughout the Critter Stack.

## Target Frameworks

JasperFx 3.x targets .NET 10.0 and .NET 11.0 (`net10.0;net11.0`). Stay on JasperFx 2.x if you still need .NET 9. See the [migration guide](/migration-guide) for what changed.

## Source Code

The source code is available on [GitHub](https://github.com/JasperFx/jasperfx).

## Getting Help

File issues on the [GitHub issue tracker](https://github.com/JasperFx/jasperfx/issues) or join the [Critter Stack Discord](https://discord.gg/WMxrvegf8H).

## Next Steps

- [Installation](/guide/installation) -- Add JasperFx to your project
- [Quick Start](/guide/quickstart) -- Get running in minutes
- [CLI Commands](/cli/) -- Build custom commands
