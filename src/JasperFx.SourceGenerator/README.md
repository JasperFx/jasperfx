# JasperFx.SourceGenerator

Roslyn source generators for [JasperFx](https://jasperfx.net), all reflection-free for fast cold start and trim/AOT-friendly builds. This single analyzer package bundles five generators:

| Generator | What it does |
|---|---|
| `DescriptionGenerator` | Generates `IDescribeMyself.ToDescription()` for `OptionsDescription` types, eliminating runtime reflection on options classes that surface in diagnostic snapshots (CritterWatch, etc.). |
| `CommandDiscoveryGenerator` | Emits a compile-time manifest of `IJasperFxCommand` types so `dotnet run -- <command>` lookups skip runtime assembly scanning. |
| `InputParserGenerator` | Emits `IGeneratedInputParser` implementations for command input models, parsing CLI arguments/flags without runtime reflection. |
| `ExtensionDiscoveryGenerator` | Emits a compile-time `JasperFx.Generated.DiscoveredExtensions` type list of `IJasperFxExtension` / `[JasperFxAssembly]`-declared extension types so framework extension loaders skip assembly scanning. |
| `ServiceRegistrationGenerator` | Emits actual `IServiceCollection` registrations (`JasperFx.Generated.GeneratedServiceRegistrations.Register`) for `[JasperFxService]`-annotated types — see below. |

> **Renamed from `JasperFx.SourceGeneration`.** The command-discovery and input-parser generators previously shipped in the `JasperFx.SourceGeneration` (noun) package, which is now retired. Reference `JasperFx.SourceGenerator` (singular) instead — its version tracks the `JasperFx` package.

## Install

```xml
<PackageReference Include="JasperFx.SourceGenerator"
                  PrivateAssets="all"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

Reference it as an analyzer/source-generator only — there's no runtime API. For `OptionsDescription`, apply the `[GenerateDescription]` attribute to a `partial` class that implements `IDescribeMyself` and a `ToDescription()` method is generated. The command-discovery and input-parser output is consumed transparently by `JasperFx.CommandLine`.

## Service registrations

Annotate a concrete class with `[JasperFxService(typeof(TService), ServiceLifetime)]` to have the
generator emit a real DI registration for it. The generator produces a per-assembly
`JasperFx.Generated.GeneratedServiceRegistrations.Register(IServiceCollection)` method of plain
`services.Add(new ServiceDescriptor(...))` calls — no reflection, trim/AOT-clean.

```csharp
[JasperFxService(typeof(IWolverineExtension), ServiceLifetime.Singleton)]
public class MyExtension : IWolverineExtension { }

// Open generic service types are closed from the implemented interface (=> IValidator<Foo>):
[JasperFxService(typeof(IValidator<>), ServiceLifetime.Scoped)]
public class FooValidator : IValidator<Foo> { }
```

Apply the attribute multiple times to register one implementation against several service types.
At startup, apply everything discovered across the loaded assemblies (reflection-free on the
generated side; a no-op when the generator wasn't run, so a reflective fallback still applies):

```csharp
JasperFx.GeneratedExtensionManifest.RegisterAllDiscoveredServices(services);
```

Only assemblies that are eligible emit a manifest — those carrying a `[JasperFxAssembly]`-derived
attribute, or executable (entry) assemblies — matching the `ExtensionDiscoveryGenerator` gate.

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
