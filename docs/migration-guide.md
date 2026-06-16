# Migration Guide

## Key Changes in 2.0.0

JasperFx 2.0 is the foundation release of the [Critter Stack 2026](https://github.com/JasperFx/jasperfx/issues/217) wave. It ships in lockstep with Marten 9.0, Wolverine 6.0, Polecat 4.0, and Weasel 9.0. Most of the changes below are mechanical pin bumps + namespace adjustments; a few API shapes changed in ways that downstream code will notice.

### Foundation

* **.NET 8 support was dropped.** JasperFx 2.0 targets `net9.0` and `net10.0`. Stay on JasperFx 1.x if you still need .NET 8.

* **`JasperFx.RuntimeCompiler` is now its own NuGet package.** The Roslyn-based runtime code generation pipeline was split out of the core `JasperFx` package so applications that pre-generate code (and want to publish AOT) don't transitively pull `Microsoft.CodeAnalysis.*` into their production deployment.

  Applications that need runtime code generation must explicitly reference the package and register the assembly generator:

  ```xml
  <PackageReference Include="JasperFx.RuntimeCompiler" Version="2.0.0-alpha.2" />
  ```

  ```csharp
  services.AddSingleton<IAssemblyGenerator, AssemblyGenerator>();
  ```

  Applications that run in `TypeLoadMode = Static` and read pre-generated code do **not** need the runtime compiler. Omitting it lets the linker drop the entire Roslyn graph in AOT publishes.

* **`ITypeLoader` abstraction.** The codegen lifecycle behind `TypeLoadMode` is now driven by a polymorphic `ITypeLoader` interface (`AutoTypeLoader` / `DynamicTypeLoader` / `StaticTypeLoader`). For most apps this is invisible — `GenerationRules.TypeLoadMode` continues to select the right loader. Hosts that registered a custom `IAssemblyGenerator` should switch to `services.AddRuntimeCompilation()` (the supported helper that wires both the loader and the generator in one call).

### Package versions

Every JasperFx-family package is at the 2.0.0 line:

| Package | 1.x | 2.0 |
|---|---|---|
| `JasperFx` | 1.31.0 | 2.0.0-alpha.8 |
| `JasperFx.Events` | 1.36.0 | 2.0.0-alpha.3 |
| `JasperFx.RuntimeCompiler` | 4.5.0 | 2.0.0-alpha.2 |
| `JasperFx.SourceGeneration` | 1.1.0 | 2.0.0-alpha.2 |
| `JasperFx.SourceGenerator` | 1.x | 2.0.0-alpha.2 |
| `JasperFx.Events.SourceGenerator` | 1.4.0 | 2.0.0-alpha.2 |

Note `JasperFx.RuntimeCompiler`'s major-version jump from 4 → 2: the package was renumbered as part of the 2.0 wave to align with the rest of the JasperFx family.

### Breaking API changes

#### `IJasperFxAggregateGrouper<TId, TQuerySession>.Group` parameter type tightened

[#201](https://github.com/JasperFx/jasperfx/issues/201) / [#202](https://github.com/JasperFx/jasperfx/pull/202). The `events` parameter is now `IReadOnlyList<IEvent>` instead of `IEnumerable<IEvent>`. Custom groupers frequently need two or more passes over the same batch (partition by type, then resolve related document IDs), and the prior `IEnumerable<IEvent>` gave no guarantee that re-iteration was safe.

```csharp
// before (JasperFx.Events 1.x)
Task Group(TQuerySession session, IEnumerable<IEvent> events, IEventGrouping<TId> grouping);

// after (JasperFx.Events 2.0)
Task Group(TQuerySession session, IReadOnlyList<IEvent> events, IEventGrouping<TId> grouping);
```

Update your `Group` implementations to the new signature; no logic change required. Defensive `events.ToList()` calls at the top of `Group` can be dropped.

The same shape applies to the lambda-form `CustomGrouping(Func<TQuerySession, IReadOnlyList<IEvent>, IEventGrouping<TId>, Task>)` overload on `EventSlicer` / `JasperFxMultiStreamProjectionBase`. Most lambda callers will compile unchanged thanks to type inference plus `IReadOnlyList<T> : IEnumerable<T>`.

#### `OptionsDescription.Children` + `Sets` are now properties, not fields

[#203](https://github.com/JasperFx/jasperfx/issues/203) / [#236](https://github.com/JasperFx/jasperfx/pull/236). `System.Text.Json`'s default options walk *properties only* — fields are skipped unless the caller sets `IncludeFields = true`. Pre-2.0 the two fields were silently dropped at the JSON boundary by every downstream serializer that didn't override the default.

Source-compatible for the typical case (the field initializers carry over to the auto-property's backing field, and no internal call site reassigns either member). Only affects code that referenced the explicit field symbol via reflection or `nameof(OptionsDescription.Children)` in field-only contexts.

#### `IInlineProjection<TOperations>.ApplyAsync` parameter widened to `IEnumerable<StreamAction>`

The streams parameter was tightened from `IEnumerable<StreamAction>` in some early shapes and is now uniformly `IEnumerable<StreamAction>` across the projection hierarchy. Third-party `IInlineProjection<TOperations>` implementors should update the signature; if your implementation needed `Count` or indexed access, materialize the parameter once with `.ToList()` at the top of the method.

```csharp
public Task ApplyAsync(TOperations operations,
                       IEnumerable<StreamAction> streams,
                       CancellationToken cancellation) { ... }
```

#### `SnapshotLifecycle` enum moved to `JasperFx.Events.Projections`

[#220](https://github.com/JasperFx/jasperfx/issues/220). The enum (`Inline` / `Async`) is canonical in `JasperFx.Events.Projections` now; pre-2.0 each consuming product carried its own copy. If your code references `Polecat.Projections.SnapshotLifecycle` or `Marten.Events.Projections.SnapshotLifecycle` (the product-side aliases), update to `JasperFx.Events.Projections.SnapshotLifecycle`. The product-side wrappers are aliased for transitional source-compat where possible.

> `ProjectionLifecycle` (the `Inline` / `Async` / `Live` enum passed to `Projections.Add<T>(...)`) moved to the same `JasperFx.Events.Projections` namespace for the same reason. `using JasperFx.Events.Projections;` covers both.

#### Convention-based projection subclasses must be declared `partial`

Conventional `Apply` / `Create` / `ShouldDelete` methods on a **projection subclass** — one that derives from `SingleStreamProjection<TDoc, TId>`, `MultiStreamProjection<TDoc, TId>`, or `EventProjection` — are now dispatched by the compile-time `JasperFx.Events.SourceGenerator`. Pre-2.0 they were dispatched by runtime reflection; **in 2.0 there is no runtime reflection fallback.** For the generator to emit its `[GeneratedEvolver]` dispatcher, the projection class must be declared `partial`, and its convention methods must be `public`.

```csharp
// before (JasperFx.Events 1.x) — reflection dispatched the conventional methods
public class TripProjection : SingleStreamProjection<Trip, Guid>
{
    public Trip Create(IEvent<TripStarted> e) => new() { Id = e.StreamId };
    public void Apply(Travel e, Trip trip) => trip.Traveled += e.TotalDistance();
}

// after (JasperFx.Events 2.0) — `partial` lets the source generator emit the dispatcher
public partial class TripProjection : SingleStreamProjection<Trip, Guid>
{
    public Trip Create(IEvent<TripStarted> e) => new() { Id = e.StreamId };
    public void Apply(Travel e, Trip trip) => trip.Traveled += e.TotalDistance();
}
```

A non-`partial` subclass with conventional methods fails fast at projection registration (e.g. inside `AddMarten(opts => opts.Projections.Add<TripProjection>(...))`) rather than on first event dispatch:

```
JasperFx.Events.Projections.InvalidProjectionException:
No source-generated dispatcher found for TripProjection. Conventional
Apply/Create/ShouldDelete methods are dispatched by the compile-time
JasperFx.Events.SourceGenerator; there is no runtime fallback.
```

**Self-aggregating snapshot types do _not_ need `partial`.** A type that carries its own `Apply`/`Create` methods and is registered via `Snapshot<T>` (or the single-arg `SingleStreamProjection<T>` / `AggregateStream<T>` self-aggregation forms) is handled without a projection subclass and needs no `partial` keyword.

Two escape hatches if the generated dispatcher is not what you want:

1. **Override the evolver directly.** Implementing `Evolve` / `EvolveAsync` / `DetermineAction` / `DetermineActionAsync` bypasses the generated dispatcher — no `partial` required.
2. **Confirm the generator ran.** It is delivered as a Roslyn analyzer and must run in the assembly that *defines the aggregate type*; make sure that reference does not strip the `analyzers` asset, then do a clean build.

**How the analyzer reaches your build is product-specific** — each product's own migration guide carries the consumer-facing detail, and they do not deliver it identically. Marten ships `JasperFx.Events.SourceGenerator` inside the `Marten` NuGet package, so a plain `Marten` reference is enough; Marten's migration guide documents the full consumer story — the _No runtime reflection fallback_ admonition, the `public`-method requirement, and the exact `InvalidProjectionException` site — at [martendb.io/migration-guide.html#inline-lambda-projection-removal](https://martendb.io/migration-guide.html#inline-lambda-projection-removal). Other consumers may deliver the generator through a different package (or require an explicit analyzer reference) — consult that product's guide. The cross-stack AOT walkthrough is [Publishing AOT with JasperFx](./codegen/aot.md).

#### `UnknownTenantIdException.TenantId` exposed as a property

[#224](https://github.com/JasperFx/jasperfx/issues/224) / [#240](https://github.com/JasperFx/jasperfx/pull/240). The exception now carries a public read-only `TenantId` property so consumers can `catch (UnknownTenantIdException ex) { ex.TenantId }` without parsing the message string. The single-argument constructor signature is unchanged; the new property is populated automatically. Purely additive — no caller-side migration needed unless you were custom-deriving from the type.

#### Codegen CLI now enforces `ServiceLocationPolicy` per file

[#227](https://github.com/JasperFx/jasperfx/issues/227) / [#239](https://github.com/JasperFx/jasperfx/pull/239). Pre-2.0 the `codegen preview` / `write` / `test` CLI paths silently bypassed the `AssertServiceLocationsAreAllowed` hook that downstream products (Wolverine, Marten) use to enforce `ServiceLocationPolicy.NotAllowed`. In 2.0 those paths fail fast.

If you set `ServiceLocationPolicy.NotAllowed` in your host options and have lambda-factory registrations that the codegen has to resolve via `IServiceProvider`, the CLI now throws `InvalidServiceLocationException` at generation time. The fix is either:

1. Switch to a constructable registration (preferred — most lambda factories can be hoisted to a constructor)
2. Add an explicit allow-list entry: `opts.CodeGeneration.AlwaysUseServiceLocationFor<T>()` (in Wolverine; the equivalent in your product)
3. Relax to `ServiceLocationPolicy.AllowedButWarn` (the pre-2.0 default behavior)

#### `ScopedContainerCreation` gates `await using` on `AsyncMode.AsyncTask`

[#228](https://github.com/JasperFx/jasperfx/issues/228) / [#238](https://github.com/JasperFx/jasperfx/pull/238). Pre-2.0 the scope-creation frame emitted `await using var scope = _serviceScopeFactory.CreateAsyncScope();` whenever `method.AsyncMode != AsyncMode.None`. That bucket includes `ReturnFromLastNode` and `ReturnCompletedTask` — both produce method declarations *without* the `async` keyword, so the emitted body was a CS4032/CS1996 compile error in those modes.

2.0 only emits `await using` when `AsyncMode == AsyncMode.AsyncTask` (the mode that produces the `async ReturnType` declaration); other modes fall through to a synchronous `using var scope = factory.CreateScope();`. Source-compatible for callers that previously hit the compile-error path (those now compile); behavior-equivalent otherwise.

### AOT publishing

JasperFx 2.0 is the first version where AOT publishing is a supported posture for apps that don't need runtime code generation:

```bash
dotnet run -- codegen write     # dev-time pre-generation
dotnet publish /p:PublishAot=true   # production publish, Static mode, no Roslyn
```

Status as of JasperFx 2.0.0-alpha.12 + JasperFx.Events 2.0.0-alpha.5:

* `JasperFx.csproj` builds with **`IsAotCompatible=true` and 0 IL warnings**. Every reflective surface either has precise `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` annotations or carries a `[DynamicallyAccessedMembers]` parameter constraint. Consumers compiling with `IsAotCompatible=true` see a precise punch-list of which JasperFx APIs are trim-hostile rather than a wall of analyzer noise.
* `JasperFx.Events.csproj` also has `IsAotCompatible=true`; the annotation propagation cascade is tracked in [#262](https://github.com/JasperFx/jasperfx/issues/262) for completion.
* `JasperFx.Core.Reflection.CloseAndBuildAs<T>` overloads carry `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]`. The AOT-friendly replacement for hot paths is `JasperFx.Core.Reflection.GenericFactoryCache` — a delegate cache keyed on type arguments. Consumers source-generate the per-type delegate factory and stay clean.
* `JasperFx.RuntimeCompiler.AssemblyGenerator` carries both attributes at the class level. Don't reference the package or call `services.AddRuntimeCompilation()` in your production publish — letting the trimmer drop the entire `Microsoft.CodeAnalysis.*` graph is the whole point of the package split.
* `DynamicTypeLoader` carries both attributes; `StaticTypeLoader` is AOT-safe by construction.
* `JasperFx.CommandLine` is annotated as a dev-time CLI surface. AOT consumers route command discovery through the `JasperFx.SourceGenerator`-emitted `DiscoveredCommands` manifest, which is read at runtime by string lookup and survives trimming.

For the full end-to-end walkthrough — package references, csproj configuration, dev-time + publish-time workflow, the `WarningsAsErrors` policy, smoke-test verification, and the cross-stack AOT story — see **[Publishing AOT with JasperFx](./codegen/aot.md)**.

### Internal changes worth noting

These don't break the public API but show up in behavior that users have asked about:

* **Deterministic codegen ordering** ([#196](https://github.com/JasperFx/jasperfx/pull/196)). Pre-generated source files are byte-identical across runs, making them a reliable build artifact for source-control commit and AOT publishing. Pre-2.0 `ImHashMap` enumeration order varied across processes because of randomized string/type hash codes.
* **`RecentlyUsedCache` thread-safety + deterministic LRU** ([#226](https://github.com/JasperFx/jasperfx/issues/226), [#231](https://github.com/JasperFx/jasperfx/issues/231)). The cache's `Store` field-level update is now serialised, and LRU eviction uses a strictly-increasing `long _tick` counter instead of `DateTimeOffset.UtcNow` (which had sub-µs ties on tight Store loops).
* **`AssemblyGenerator` tolerates missing referenced assemblies** ([#188](https://github.com/JasperFx/jasperfx/issues/188)). Oracle.ManagedDataAccess.Core lists six platform-conditional satellites of which a given deployment typically pulls in zero or one. Pre-2.0 a missing satellite aborted the parent assembly's reference setup and surfaced as cascading runtime-compile failures; 2.0 skips and continues.
* **Package metadata** ([#221](https://github.com/JasperFx/jasperfx/issues/221), [#222](https://github.com/JasperFx/jasperfx/issues/222)). The deprecated `PackageIconUrl` is replaced with the modern `PackageIcon` + embedded `logo.jpg`; `PackageReadmeFile` packs each project's per-package README into its `.nupkg`.

### Dependency lockstep

JasperFx 2.0 ships with the rest of the Critter Stack 2026 wave. Supported pairings:

| JasperFx | JasperFx.Events | JasperFx.RuntimeCompiler | Marten | Wolverine | Polecat | Weasel |
|---|---|---|---|---|---|---|
| 2.0 | 2.0 | 2.0 | 9.0 | 6.0 | 4.0 | 9.0 |

Mixing major versions across products is unsupported in this wave (the dedup work moves abstractions between assemblies and ABI-binds them to specific majors). If you upgrade JasperFx to 2.0, plan to upgrade Marten / Wolverine / Polecat to their respective 2026-wave majors at the same time.

## References

* [Critter Stack 2026 umbrella](https://github.com/JasperFx/jasperfx/issues/217)
* [JasperFx 2.0 master plan](https://github.com/JasperFx/jasperfx/issues/215)
* [JasperFx.Events 2.0 master plan](https://github.com/JasperFx/jasperfx/issues/216)
* [Cold-start pillar](https://github.com/JasperFx/jasperfx/issues/212)
* [AOT compliance pillar](https://github.com/JasperFx/jasperfx/issues/213)
