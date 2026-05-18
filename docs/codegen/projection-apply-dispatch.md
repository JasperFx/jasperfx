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

## Decisions (resolved)

1. **Inline-lambda registration is out of scope for #276.** The
   `CreateEvent` / `DeleteEvent` / `ProjectEvent` / `Project<TEvent>(handler)` /
   `ProjectAsync<TEvent>(handler)` APIs will be removed entirely in 2.0 under a
   follow-up tracked at [JasperFx/jasperfx#286](https://github.com/JasperFx/jasperfx/issues/286)
   (and Marten 9.0 migration notes at [JasperFx/marten#4467](https://github.com/JasperFx/marten/issues/4467)).
   Until that lands, the four inline-lambda FEC sites (#1, #2, #3, #16) stay on
   FEC and the surrounding class-level `IL2026` / `IL3050` suppressions remain
   in place.
2. **`AggregateVersioning` keeps FEC, routed through `LambdaBuilder`.** The
   site is rewritten to compose `LambdaBuilder.Setter<T, int>` /
   `LambdaBuilder.Setter<T, long>` with a plain closure that reads
   `IEvent.Version` / `IEvent.Sequence`. `LambdaBuilder` already compiles via
   `CompileFast` under the hood, so the AOT compromise is identical to what
   exists today — the change is structural, not behavioural. The
   class-level `IL2026` / `IL3050` suppressions on
   `AggregateVersioning.cs` stay.
3. **`partial` is required only on `EventProjection` /
   `SingleStreamProjection` / `MultiStreamProjection` types that use
   conventional methods.** Types that do not use conventional methods
   (e.g. those that override `ApplyAsync` or `Evolve*` directly) do not need
   to be `partial`. The fail-fast exception message at registration spells
   out this narrower condition.

## What ships in the #276 PR

| Action | Files | Notes |
|---|---|---|
| **Delete FEC method-discovery dispatch** in `AggregateApplication` | `AggregateApplication.Applies.cs`, `AggregateApplication.Creating.cs`, `AggregateApplication.ShouldDelete.cs` | Sites #4, #5, #6, #7, #8 above. `ApplyAsync` / `Create` now return the default / throw for event types not pre-registered by the inline-lambda path; the SG-emitted override on the projection class catches the live dispatch path. |
| **Delete FEC method-discovery dispatch** in `EventProjectionApplication` | `EventProjectionApplication.cs` | Sites #10–#15 above. The `Project<TEvent>` / `ProjectAsync<TEvent>` inline-lambda path (#16) stays. |
| **Refactor `AggregateVersioning.buildAction`** to use `LambdaBuilder` | `AggregateVersioning.cs` | Site #9. Same FEC under the hood, no direct `Expression.Lambda(...).CompileFast()` in this file any more. |
| **Drop FEC method-discovery class-level suppressions** | `AggregateApplication.cs` | Once method-discovery FEC is gone, `IL2026` / `IL3050` suppressions on `AggregateApplication.cs` are scoped down to just what `Register.cs` (inline lambdas) still justifies. |
| **Add fail-fast assertion at registration** | `JasperFxAggregationProjectionBase.AssembleAndAssertValidity`, `JasperFxEventProjectionBase.AssembleAndAssertValidity` | Throws when a `Single/Multi/Event` projection has conventional methods declared on its body but the SG override is missing. Error names the type, points at `JasperFx.Events.SourceGenerator`, and notes that the projection class must be `partial`. |
| **Equivalence tests** | `src/EventTests/Aggregation/` | Per the brief. |
| **AOT smoke verification** | `src/JasperFx.AotSmoke` | Confirm `dotnet build` still clean. |
| **Version bump** | `Directory.Packages.props` etc. | `JasperFx.Events` + `JasperFx.Events.SourceGenerator` lockstep alpha bump. |

## What stays on FEC after #276 lands

- Inline-lambda registration sites #1, #2, #3, #16 — removed under #286.
- `AggregateVersioning` — version-property setter via `LambdaBuilder`.
- `IEvent.cs` line 261, `StreamAction.cs` line 516 — strong-typed-id converters,
  out of scope for #276.
- `Tags/TagTypeRegistration.cs`, `Internals/MethodSlot.cs`,
  `Protected/StreamCompactingRequest.cs` — orthogonal sites, not projection
  dispatch.

The `FastExpressionCompiler` package reference stays as long as any of these
sites do. It is not dropped under #276.
