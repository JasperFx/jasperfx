# JasperFx.RuntimeCompiler

Roslyn-based runtime code-generation backend for the [Critter Stack](https://jasperfx.net). Compiles in-memory C# at runtime and loads the resulting assembly into the host process — used by Marten, Wolverine, and Polecat for dynamic code paths (LINQ compilation, message handlers, document storage).

Installing this package is **opt-in**. Apps that pre-generate all code (`dotnet run -- codegen write`) and run in `TypeLoadMode.Static` can omit `JasperFx.RuntimeCompiler` entirely and ship without Roslyn in the production bundle — the foundation for `PublishAot=true` deployments.

## Usage

Register the runtime compiler service in DI to opt in:

```csharp
services.AddSingleton<IAssemblyGenerator, AssemblyGenerator>();
```

When `GenerationRules.TypeLoadMode` is `Dynamic` or `Auto`, codegen will route through `AssemblyGenerator` to compile in-memory.

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
