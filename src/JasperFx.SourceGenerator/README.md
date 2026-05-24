# JasperFx.SourceGenerator

Roslyn source generators for [JasperFx](https://jasperfx.net), all reflection-free for fast cold start and trim/AOT-friendly builds. This single analyzer package bundles three generators:

| Generator | What it does |
|---|---|
| `DescriptionGenerator` | Generates `IDescribeMyself.ToDescription()` for `OptionsDescription` types, eliminating runtime reflection on options classes that surface in diagnostic snapshots (CritterWatch, etc.). |
| `CommandDiscoveryGenerator` | Emits a compile-time manifest of `IJasperFxCommand` types so `dotnet run -- <command>` lookups skip runtime assembly scanning. |
| `InputParserGenerator` | Emits `IGeneratedInputParser` implementations for command input models, parsing CLI arguments/flags without runtime reflection. |

> **Renamed from `JasperFx.SourceGeneration`.** The command-discovery and input-parser generators previously shipped in the `JasperFx.SourceGeneration` (noun) package, which is now retired. Reference `JasperFx.SourceGenerator` (singular) instead — its version tracks the `JasperFx` package.

## Install

```xml
<PackageReference Include="JasperFx.SourceGenerator"
                  PrivateAssets="all"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

Reference it as an analyzer/source-generator only — there's no runtime API. For `OptionsDescription`, apply the `[GenerateDescription]` attribute to a `partial` class that implements `IDescribeMyself` and a `ToDescription()` method is generated. The command-discovery and input-parser output is consumed transparently by `JasperFx.CommandLine`.

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
