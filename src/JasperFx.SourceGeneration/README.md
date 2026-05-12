# JasperFx.SourceGeneration

Roslyn source generator for [JasperFx](https://jasperfx.net) CLI command discovery. Generates a compile-time command registry from `IJasperFxCommand<T>` implementations in the application assembly — eliminating runtime assembly scanning for `dotnet run -- <command>` lookups.

Used by hosts that want fast cold-start CLI invocations (no reflection-based command discovery).

## Install

```xml
<PackageReference Include="JasperFx.SourceGeneration"
                  PrivateAssets="all"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

Reference it as an analyzer/source-generator only — there's no runtime API. The generated registry is consumed transparently by `JasperFx.CommandLine`.

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
