# Projection apply-method dispatch — FEC → SG audit (issue #276)

This document enumerates every runtime FastExpressionCompiler (FEC) site in the
`JasperFx.Events` projection-apply dispatch code path and maps each to the
matching emit from `JasperFx.Events.SourceGenerator` (SG). Produced as the
first investigation deliverable for [JasperFx#276](https://github.com/JasperFx/jasperfx/issues/276)
before any code is removed.

## In scope

- `src/JasperFx.Events/Aggregation/AggregateApplication.{Register,Applies,Creating,ShouldDelete}.cs`
- `src/JasperFx.Events/Aggregation/AggregateVersioning.cs`
- `src/JasperFx.Events/Projections/EventProjectionApplication.cs`

## Out of scope

- `src/JasperFx.Events/IEvent.cs:261` — strong-typed-id converter, not projection dispatch.
- `src/JasperFx.Events/StreamAction.cs:516` — strong-typed-id converter, not projection dispatch.
- `src/JasperFx.Events/Tags/TagTypeRegistration.cs:39` — value-type wrapper/unwrapper for tag types, not projection dispatch.
- `src/JasperFx.Events/Internals/MethodSlot.cs:14` — orthogonal slot invocation path.
- `src/JasperFx.Events/Protected/StreamCompactingRequest.cs:70` — compactor aggregator graph plumbing, not projection dispatch.

Of these, only the strong-typed-id converters in `IEvent.cs` / `StreamAction.cs`
are guaranteed to keep the `FastExpressionCompiler` package reference alive
after #276 lands. The other off-scope sites can be revisited later.

## How the SG plugs into runtime today

The SG emits one of five shapes through `EvolverCodeEmitter`
(`src/JasperFx.Events.SourceGenerator/EvolverCodeEmitter.cs`):

| Mode | Emits | How runtime picks it up |
|---|---|---|
| `PartialProjection` | Partial method on the user's projection class (`Evolve` / `EvolveAsync` / `DetermineActionAsync`) | `JasperFxAggregationProjectionBase.isOverridden(...)` returns `true` for the generated override and `_usesConventionalApplication = false` — `AggregateApplication` is never invoked for dispatch. |
| `SelfAggregating` | Standalone evolver class implementing `IGeneratedSyncEvolver<TDoc,TId>` or `IGeneratedSyncDetermineAction<TDoc,TId>` + an assembly-level `[GeneratedEvolverAttribute]` | `JasperFxAggregationProjectionBase.tryUseAssemblyRegisteredEvolver(...)` activates and binds the evolver. |
| `SelfAggregatingEvolve` | Standalone evolver class implementing `IGeneratedAsyncEvolver<TDoc,TId>` + `[GeneratedEvolverAttribute]` | Same as above. |
| `EventProjection` | Partial `ApplyAsync(TOperations, IEvent, CancellationToken)` override on the user's `JasperFxEventProjectionBase<TOperations>` subclass | The override wins at vtable dispatch. `EventProjectionApplication` is only used when the base `ApplyAsync` is not overridden. |
| `EventProjectionTypeRegistrationOnly` | Partial constructor that calls `RegisterPublishedType<T>()` for every emitted type | Bookkeeping; does not change dispatch. |

So the runtime contract is: **if the SG ran against the user's assembly and the
projection / aggregate class is `partial`, the FEC path in
`AggregateApplication` / `EventProjectionApplication` is never exercised for
dispatch.** The FEC path is the fallback for assemblies the SG has not run
against, or for projection classes that are not `partial`.

Issue #276's doctrine for 2.0 flips the contract: SG is the only path, and we
fail fast at registration when no `[GeneratedEvolver]` is found. The runtime
classes lose their FEC-built dispatchers entirely.

## FEC site audit

Counts in the brief total 15 distinct `CompileFast()` / `Expression.Compile()`
call sites across the listed files. Below each is grouped by the shape it
covers and tagged with how the SG output compares today.

### `Aggregation/AggregateApplication.Register.cs` — runtime inline-lambda registration

| # | Site | Shape | SG coverage |
|---|---|---|---|
| 1 | [`createEvent` line 53](../../src/JasperFx.Events/Aggregation/AggregateApplication.Register.cs#L53) | Wraps a user-supplied `Func<TEvent, ...>` or `Func<TEvent, TQuerySession, Task<TAggregate>>` registered via `CreateEvent<TEvent>(handler)` | **SG cannot cover.** The handler is a runtime value, not a method on a discoverable type. Replacement is a closure-based dispatcher (no `CompileFast`, just `(_, e, s, t) => handler(...)`), not an SG hookup. |
| 2 | [`deleteEvent` line 106](../../src/JasperFx.Events/Aggregation/AggregateApplication.Register.cs#L106) | Wraps a user-supplied `Func<...,bool>` / `Func<...,Task<bool>>` registered via `DeleteEvent<TEvent>(handler)` | Same as above — closure rewrite. |
| 3 | [`projectEvent` line 188](../../src/JasperFx.Events/Aggregation/AggregateApplication.Register.cs#L188) | Wraps a user-supplied apply-style handler registered via `ProjectEvent<TEvent>(handler)` | Same as above — closure rewrite. |

**Gap.** These three sites are inline-lambda registration APIs. The SG is
compile-time and only sees methods on classes. It cannot emit a dispatcher for
a lambda the user passes to `ProjectEvent<TEvent>(handler)` at runtime. The
correct replacement is a closure-based wrapper (cast `IEvent` to
`IEvent<TEvent>`, read `.Data`, invoke the handler) — slightly slower than the
FEC-compiled delegate but no runtime codegen. **This needs a doctrine clarification
on the issue.** Either:

1. Keep the inline-lambda registration API and rewrite these three sites using
   closures, **not** SG. The PR title's "depend on the source generator
   instead" is then a half-truth for these three.
2. Deprecate the inline-lambda registration API in 2.0 and require everything
   to come through methods on a partial class.

### `Aggregation/AggregateApplication.Applies.cs` — discovery-style `Apply` dispatch

| # | Site | Shape | SG coverage |
|---|---|---|---|
| 4 | [`determineApplication` line 79](../../src/JasperFx.Events/Aggregation/AggregateApplication.Applies.cs#L79) | Reflects `Apply` method on aggregate/projection by event type and compiles `(snapshot, IEvent, session, ct) -> ValueTask<TAggregate?>` | **Covered.** `EvolverCodeEmitter.EmitEvolveOverride` / `EmitEvolveAsyncOverride` / `EmitDetermineActionAsyncOverride` emit equivalent dispatch for the projection-class case (`PartialProjection`). `EmitSelfAggregatingEvolver` / `EmitSelfAggregatingEvolveEvolver` cover the self-aggregating case. Both paths handle the same parameter shapes (`TEvent`, `IEvent<TEvent>`, `TQuerySession`, `CancellationToken`, `IEvent`). |

### `Aggregation/AggregateApplication.Creating.cs` — `Create` and default-ctor dispatch

| # | Site | Shape | SG coverage |
|---|---|---|---|
| 5 | [`determineCreator` line 77](../../src/JasperFx.Events/Aggregation/AggregateApplication.Creating.cs#L77) | Compiles a `Func<TAggregate>` factory from `TAggregate`'s parameterless ctor when no `Create` method exists | **Covered.** The SG-emitted `EmitNullSnapshotCases` / `EmitNullSnapshotCasesAsync` produce `snapshot = new TAggregate()` directly in the switch arm. |
| 6 | [`determineCreator` line 90](../../src/JasperFx.Events/Aggregation/AggregateApplication.Creating.cs#L90) | Compiles an `Apply` lambda used as a "create from default + apply event" path when no explicit `Create` exists | **Covered** — same SG emit path, the `new TAggregate()` is followed by the same `Apply` call the regular apply path emits. |
| 7 | [`determineCreator` line 119](../../src/JasperFx.Events/Aggregation/AggregateApplication.Creating.cs#L119) | Reflects `Create` method (instance or static, on aggregate or projection) and compiles `(IEvent, session, ct) -> ValueTask<TAggregate>` | **Covered.** `EmitCreateCall` / `EmitCreateCallSync` / `EmitSelfAggregatingCreateCall` cover instance and static methods on both aggregate and projection classes, sync and async, with/without `TQuerySession`, with/without `CancellationToken`. |

### `Aggregation/AggregateApplication.ShouldDelete.cs` — `ShouldDelete` dispatch

| # | Site | Shape | SG coverage |
|---|---|---|---|
| 8 | [`tryBuildShouldDelete` line 19](../../src/JasperFx.Events/Aggregation/AggregateApplication.ShouldDelete.cs#L19) | Reflects `ShouldDelete` method by event type and compiles `(snapshot, IEvent, session, ct) -> ValueTask<bool>` | **Covered.** `EmitDetermineActionAsyncOverride` + `EmitShouldDeleteCall` produce the same boolean dispatch as part of the `DetermineActionAsync` switch. SG only fires this path when the projection class itself has `ShouldDelete` methods on the aggregate type; the projection-class case routes through `PartialProjection` and works identically. |

### `Aggregation/AggregateVersioning.cs` — version-property setter

| # | Site | Shape | SG coverage |
|---|---|---|---|
| 9 | [`buildAction` line 116](../../src/JasperFx.Events/Aggregation/AggregateVersioning.cs#L116) | Compiles `Action<T, IEvent>` that sets the aggregate's `Version` property/field from `IEvent.Version` (single-stream) or `IEvent.Sequence` (multi-stream), with a `Convert.ToInt32` on `int` members | **Gap.** The SG does not emit a version-setter at all today. The dispatch is on a property/field, not on a method, so it's a fundamentally different emitter shape from anything in `EvolverCodeEmitter`. Two options: (a) a new SG mode `EmitVersionSetter` that emits an `IGeneratedVersionSetter<T>` and a `[GeneratedEvolverAttribute]`-style assembly registration; (b) rewrite this site to use `PropertyInfo.SetValue` / `FieldInfo.SetValue` (reflective, no runtime codegen — `IL3050` clean even without SG). Option (b) is simpler and the perf cost is small because `TrySetVersion` is called once per aggregate per slice, not per event. |

### `Projections/EventProjectionApplication.cs` — `EventProjection` dispatch

| # | Site | Shape | SG coverage |
|---|---|---|---|
| 10 | [`buildApplication` line 145](../../src/JasperFx.Events/Projections/EventProjectionApplication.cs#L145) | `ValueTask`-returning `Project` method | **Covered.** `EmitEventProjectionApplyAsync` emits `case TEvent data: await Project(args); break;` with `IsAsync` and parameter-shape detection. |
| 11 | [`buildApplication` line 152](../../src/JasperFx.Events/Projections/EventProjectionApplication.cs#L152) | `Task`-returning `Project` method | **Covered** — same SG emit path. |
| 12 | [`buildApplication` line 157](../../src/JasperFx.Events/Projections/EventProjectionApplication.cs#L157) | `void`-returning `Project` method | **Covered** — same SG emit path. |
| 13 | [`CreatorBuilder<T>.Build` line 357](../../src/JasperFx.Events/Projections/EventProjectionApplication.cs#L357) | Synchronous `Create` / `Transform` returning `T` | **Covered.** `EmitEventProjectionCreateCall` emits `storeEntity(operations, Create(args));`. |
| 14 | [`CreatorBuilder<T>.Build` line 370](../../src/JasperFx.Events/Projections/EventProjectionApplication.cs#L370) | `ValueTask<T>`-returning `Create` / `Transform` | **Covered** — same SG emit path with `await`. |
| 15 | [`CreatorBuilder<T>.Build` line 380](../../src/JasperFx.Events/Projections/EventProjectionApplication.cs#L380) | `Task<T>`-returning `Create` / `Transform` | **Covered** — same SG emit path with `await`. |
| 16 | [`projectEvent` line 282 / 290 / 297](../../src/JasperFx.Events/Projections/EventProjectionApplication.cs#L282) | Inline-lambda registration via `Project<TEvent>(handler)` / `ProjectAsync<TEvent>(handler)` | **SG cannot cover** — same situation as the Register.cs sites above. Closure rewrite required. |

(The brief's "× 6" count for `EventProjectionApplication.cs` rolls up to the seven distinct
`CompileFast()` calls grouped by intent; the three sync/Task/ValueTask cases in `buildApplication`,
the three return-shape cases in `CreatorBuilder<T>.Build`, and the lambda registration. Audit
keeps them split because they map to different SG emit decisions.)

## Coverage summary

| Bucket | Sites | SG already covers | Gap |
|---|---|---|---|
| Method-discovery `Apply` / `Create` / `ShouldDelete` on aggregate or projection (Aggregation) | 5 | 5 | 0 |
| `EventProjection` `Project` / `Create` / `Transform` method discovery | 6 | 6 | 0 |
| Inline-lambda registration (`CreateEvent` / `DeleteEvent` / `ProjectEvent` / `Project`) | 4 | 0 | 4 — needs closure-based rewrite |
| `Version` member setter | 1 | 0 | 1 — needs new SG emitter, or reflective fallback |

**11 of 16 sites** are direct equivalents to existing SG output. **4 sites**
(inline-lambda registration in `Register.cs` + the `projectEvent` lambda in
`EventProjectionApplication.cs`) cannot be replaced by SG by design — the
handler is a runtime closure, not a discoverable method. **1 site**
(`AggregateVersioning`) is not covered by SG today and would need either a new
emit shape or a reflective rewrite.

## Decisions (resolved and applied)

1. **Inline-lambda registration removed in this PR (folds in #286).** The
   `CreateEvent` / `DeleteEvent` / `ProjectEvent` / `Project<TEvent>(handler)` /
   `ProjectAsync<TEvent>(handler)` APIs are deleted from the JasperFx.Events
   public surface. Marten 9.0 migration advice is tracked at
   [JasperFx/marten#4467](https://github.com/JasperFx/marten/issues/4467).
2. **`AggregateVersioning` routes through `LambdaBuilder`, which is now
   AOT-aware.** `LambdaBuilder.{Setter, Getter, GetProperty, SetProperty,
   GetField, SetField}` branch on `RuntimeFeature.IsDynamicCodeSupported`:
   FEC under a JIT runtime (same perf as before), reflective `PropertyInfo.SetValue`
   / `FieldInfo.SetValue` under NativeAOT (correct, no crash). The
   class-level `IL2026` suppression on `AggregateVersioning.cs` stays
   because the JIT branch still uses FEC; `IL3050` is no longer needed at
   the call site because the dynamic-code requirement moved to the private
   `LambdaBuilder.Compiled*` helpers.
3. **`partial` is required only on `EventProjection` /
   `SingleStreamProjection` / `MultiStreamProjection` types that use
   conventional methods.** Types that do not use conventional methods
   (e.g. those that override `ApplyAsync` or `Evolve*` directly) do not need
   to be `partial`. The fail-fast exception message at registration spells
   out this narrower condition.

## What shipped in #276

| Action | Files | Notes |
|---|---|---|
| **AOT-aware `LambdaBuilder`** | `src/JasperFx/Core/Reflection/LambdaBuilder.cs` | Every public accessor (`Getter`/`Setter`/`GetProperty`/`SetProperty`/`GetField`/`SetField`) branches on `RuntimeFeature.IsDynamicCodeSupported`. JIT runtime: FEC. NativeAOT: reflective `GetValue`/`SetValue`. `[RequiresDynamicCode]` moves to the private `Compiled*` helpers. |
| **`AggregateVersioning` routed through `LambdaBuilder`** | `src/JasperFx.Events/Aggregation/AggregateVersioning.cs` | Site #9. Picks up the AOT-aware behaviour automatically. |
| **Inline-lambda APIs removed** (folds in #286) | `AggregateApplication.Register.cs` (deleted), `IAggregationSteps.cs` trimmed to `DeleteEvent<TEvent>()`/`TransformsEvent<TEvent>()`, `JasperFxAggregationProjectionBase.AggregationSteps.cs` matching trim, `EventProjectionApplication` `Project<T>`/`ProjectAsync<T>`/`projectEvent` deleted, `JasperFxEventProjectionBase` `Project<T>`/`ProjectAsync<T>` wrappers deleted | Sites #1, #2, #3, #16. |
| **FEC method-discovery dispatch deleted** | `AggregateApplication.Applies.cs`, `AggregateApplication.Creating.cs`, `AggregateApplication.ShouldDelete.cs` (all deleted); `AggregateApplication.cs` rewritten as validity-only. `EventProjectionApplication.cs` FEC paths (`determineApplication` / `buildApplication` / `buildCreator` / `CreatorBuilder<T>`) deleted; class trimmed to validity-only. | Sites #4–#8, #10–#15. Hot-path dispatch is now exclusively the source-generated `Evolve` / `EvolveAsync` / `DetermineActionAsync` / `ApplyAsync` override on the user's partial projection class. |
| **Class-level FEC suppressions dropped** | `AggregateApplication.cs`, `EventProjectionApplication.cs` | `IL2026` / `IL3050` suppressions on these classes are gone because their FEC paths are gone. `AggregateVersioning.cs` keeps `IL2026` because the JIT branch of `LambdaBuilder` still uses FEC internally. |
| **`[GeneratedCodeAttribute]` on partial-projection overrides** | `src/JasperFx.Events.SourceGenerator/EvolverCodeEmitter.cs` (`EmitEvolveOverride`, `EmitEvolveAsyncOverride`, `EmitDetermineActionAsyncOverride`) | Matches what `EventProjection` mode already does. Runtime uses this to distinguish source-generated dispatch from user-authored overrides in `AssembleAndAssertValidity`. |
| **Fail-fast at registration** | `JasperFxAggregationProjectionBase.AssembleAndAssertValidity`, `JasperFxEventProjectionBase.AssembleAndAssertValidity` | Throws when a `Single/Multi/Event` projection has conventional methods declared but no source-generated dispatcher is in place. Error names the type, points at `JasperFx.Events.SourceGenerator`, and notes that the projection class must be `partial`. Source-generated override + conventional methods is the supported pairing; user override + source-generated override is rejected as a configuration conflict. |
| **Runtime backstop** | `JasperFxAggregationProjectionBase.Runtime.cs` virtual `EvolveAsync` body; `EventProjectionApplication.ApplyAsync` body; `AggregateApplication.BuildAsync` body | All throw the same fail-fast message. Hot-path SG override always wins at vtable dispatch; these bodies are only reached if a projection is exercised outside the registration flow that runs the fail-fast. |
| **Test-suite cascade** | `EventTests.csproj` references the SG as an analyzer; conventional-method fixtures are `partial`; `LambdaEventProjection` / `InlineProjection` fixtures deleted; `AggregateApplicationTests.cs` deleted (it tested the now-gone FEC dispatch surface); colliding `EventTests.EventTests` class renamed to `EventBasicsTests` so the SG resolves `EventTests.Projections.*` correctly. |
| **Version bump** | `JasperFx.Events.csproj` 2.0.0-alpha.10 → 2.0.0-alpha.11, `JasperFx.Events.SourceGenerator.csproj` 2.0.0-alpha.2 → 2.0.0-alpha.3 |

## What stays on FEC after #276 lands

- `AggregateVersioning` — version-property setter via `LambdaBuilder`,
  which uses FEC on JIT and reflection on AOT.
- `IEvent.cs` line 261, `StreamAction.cs` line 516 — strong-typed-id
  converters, out of scope for #276. Both `[RequiresDynamicCode]`
  unchanged.
- `Tags/TagTypeRegistration.cs`, `Internals/MethodSlot.cs`,
  `Protected/StreamCompactingRequest.cs` — orthogonal sites, not projection
  dispatch. Could be made AOT-aware in a follow-up.

The `FastExpressionCompiler` package reference stays as long as any of these
sites do. It is not dropped under #276.

## Known follow-up: source generator should emit `global::` prefixes

While porting the EventTests suite to the source-generator path, the generator
produced uncompilable code in the presence of a same-named class shadowing a
namespace lookup (e.g. a `class EventTests` inside `namespace EventTests`
collides with the parent namespace when the generated file is inside
`namespace EventTests.Projections;`). The pragmatic fix in this PR was to
rename the colliding test class. A proper fix — making
`EvolverCodeEmitter.ToDisplayString()` call sites consistently use
`SymbolDisplayFormat.FullyQualifiedFormat` so generated output uses
`global::` prefixes everywhere — is a separate item.
