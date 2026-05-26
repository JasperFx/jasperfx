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

## Performance

The generators replace runtime reflection with code emitted at compile time. Measured against
the reflection fallback with BenchmarkDotNet (`src/CommandLineBenchmarks`, Apple M5 Max, .NET 9):

| Workload | Reflection | Generated |
|---|---|---|
| Build handlers for a small input model | ~2.8 µs | ~0.43 µs (≈6.5× faster) |
| Build handlers for a large input model | ~8.5 µs | ~0.48 µs (≈18× faster) |
| Full `dotnet run -- <command>` parse | baseline | ≈2–4.5× faster, ≈2–3× fewer allocations |

The win is eliminating reflection (`PropertyInfo.SetValue`, converter lookups, assembly scanning),
not eliminating allocation — the generated parser still allocates its handler list and delegates.
Earlier "thousands of times faster / zero allocation" figures were a measurement artifact: the
generators were emitting nothing, so the benchmark compared reflection against a no-op. The numbers
above reflect the generators actually running.

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
