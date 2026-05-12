# JasperFx

Foundational helpers and command line support used by [JasperFx](https://jasperfx.net) and the [Critter Stack](https://jasperfx.net) projects (Marten, Wolverine, Polecat, Weasel).

Provides:

- **`JasperFx.CommandLine`** — a Spectre-Console-backed CLI framework (formerly the Oakton library), the host for `dotnet run -- <command>` style tooling.
- **`JasperFx.CodeGeneration`** — Roslyn-backed runtime code generation, with pluggable `ITypeLoader` strategies (`Static` / `Dynamic` / `Auto`) for AOT-friendly deployments.
- **`JasperFx.Core`** — reflection / type-scanning helpers, IoC primitives, the `GenericFactoryCache` hot-path delegate cache, and `RecentlyUsedCache` LRU.
- **`JasperFx.MultiTenancy`** — shared tenant-id abstractions (`IHasTenantId`, `TenantId`, `TenantIdStyle`) consumed across the Critter Stack.
- **`JasperFx.Descriptors`** — diagnostic descriptor types that products like CritterWatch use to render configuration / capability snapshots.

## Quick start

```csharp
// CLI host
return await new HostBuilder()
    .ConfigureServices(services => services.AddJasperFxCommands())
    .RunJasperFxCommandsAsync(args);
```

```csharp
// Codegen
var rules = new GenerationRules
{
    TypeLoadMode = TypeLoadMode.Static,
    GeneratedNamespace = "MyApp.Generated"
};
```

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
