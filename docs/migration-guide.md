# Migration Guide

## Key Changes in 3.0.0

JasperFx 3.0 is the foundation of the Critter Stack .NET 11 release train, alongside Weasel 10, Marten 10, Polecat 6, Fisher 2 and Wolverine 7.

::: warning In progress
This section grows as the 3.0 work lands, so it describes the `3.0` branch rather than a released package.
:::

### Foundation

* **.NET 9 support was dropped.** JasperFx 3.0 targets `net10.0`. `net11.0` joins it once the .NET 11 SDK is on the build machines. Stay on JasperFx 2.x if you still need .NET 9.

* **`JasperFx.RuntimeCompiler` moves to 6.0.0**, because it follows the same target framework change and depends on JasperFx 3.

* **`Microsoft.Extensions.*` packages have a 10.0.0 floor.**

* **`JasperFx.RuntimeCompiler` compiles with Roslyn 5.9**, and **`FastExpressionCompiler` moves to 6.0**, which itself drops `netstandard2.0` and `net9.0` and renames `ClosureInfo` to `CompilerContext`. Neither name appears in JasperFx's public API, but code of yours that uses FastExpressionCompiler directly will meet the rename.

### Testing

* **`JasperFx.Events.ComplianceTests` depends on xunit.v3 4.x**, which runs on Microsoft.Testing.Platform 2.x. A test project that enrolls in the compliance suites has to move to xunit.v3 4.x with it, because 3.x beside platform 2.x fails at run time. Add `"test": { "runner": "Microsoft.Testing.Platform" }` to `global.json`, and pass a project to `dotnet test` with `--project` rather than as a positional path.

### Changed defaults

* **`GateSideEffectsBehindPriorVersion` is on by default for every event store** ([#1075](https://github.com/JasperFx/jasperfx/issues/1075)).

  When a new version of a projection (`Version > 1`) starts behind the highest prior version's progress during a blue/green or rolling deploy, it now catches up to that version's mark with side effects suppressed. `RaiseSideEffects`, raised events and published messages only fire for events the prior version never processed. Before 3.0 the new version emitted them over the whole replay, so the same messages could go out twice.

  * **Schema effect:** none. The gate reads the existing progression rows.
  * **Cost:** each start of a `Version > 1` shard reads the store's projection progress once. Projections at version 1 do not pay this, and neither do shards that subscribe from the present, which have no prior replay to gate.
  * **Pacing:** a gated shard starts normally and reports `ShardState.SideEffectsSuppressed` while it warms up. `DaemonSettings.MaxConcurrentSideEffectGateWarmupsPerDatabase` limits how many warm up at once.
  * **Opt out:** set `GateSideEffectsBehindPriorVersion = false` on the projection, or use your store's restore helper (`RestoreV9Defaults()` in Marten, and the Polecat 6 and Fisher 2 equivalents) to put every changed default back.

  A shard that subscribes from the present used to log a warning when this was set. It now logs at `Information`, since the combination is no longer one anyone chose.

* **`AsyncOptions.MaximumHopperSize` now defaults to 2 x `BatchSize`** instead of a flat 5,000 ([#1076](https://github.com/JasperFx/jasperfx/issues/1076)).

  A shard that is behind keeps loading pages until it holds this many events, and those pages then wait for a write slot, so memory followed behind shards x 5,000 events however small `BatchSize` was. Shrinking `BatchSize` to save memory made that worse, because it meant more pages of prefetch per shard.

  * **Effect:** far less memory on write-bound shards at the default `BatchSize` of 500. Measured against the real agent with a simulated database, peak in-flight events fell by 77% with no measurable change in throughput.
  * **Larger batches:** the default follows `BatchSize` in both directions, so a `BatchSize` above 2,500 now gets a deeper hopper than it had before. Set `MaximumHopperSize` if you tuned around the old cap.
  * **Schema effect:** none.
  * **Opt out:** set `MaximumHopperSize` explicitly, which always wins over the derived default. `MaximumHopperSize = 5000` restores the old behavior.

### For event store authors

The types below were lifted out of Marten, Polecat and Fisher so that each store can delete its own copy. Adopting them is expected to land in the same change as the JasperFx 3.0 pin bump: where a store keeps a type of the same name, any file importing both namespaces stops compiling with `CS0104`.

* **`EventBoundary<T>` and `TagValueStringifier` are now in `JasperFx.Events.Tags`** ([#1044](https://github.com/JasperFx/jasperfx/issues/1044)).

  Build the boundary from `FetchForWritingByTags`, and pass the same `IEventOperations` the fetch went through, because that decides the tenant of what it appends. Under `ForTenant` that means the tenant-scoped operations, not the root session's. The boundary does not enforce consistency: the store still checks `LastSeenSequence` at `SaveChangesAsync`.

  Behavior that differs from the stores' own copies:

  * An event routed by a tag stamps the tag's aggregate type on a stream that has none.
  * On a Guid-identity store, a string or numeric tag no longer names a stream. The next tag that can name one does, and failing that the event gets its own new stream.
  * On a string-identity store, any tag value names a stream, in invariant form.
  * A `Guid.Empty` tag value reaches the store's `Append` guard rather than being accepted.
  * Under Marten's `UseMandatoryStreamTypeDeclaration`, an event no tag can route now throws `StreamTypeMissingException` at `AppendOne`.

* **`EventRegistry` now owns masking rules, binary serializers, tag types and the name and alias caches** ([#1043](https://github.com/JasperFx/jasperfx/issues/1043)).

  Override `AliasFor` rather than `AggregateAliasFor`, which is now the caching wrapper around it. A store that keeps overriding `AggregateAliasFor` must move both alias overrides together, or `AggregateTypeFor` and `TryResolveAggregateType` come back empty. Call `RegisterDotNetTypeNames` wherever you build an event mapping: stores override `EventMappingFor` and never reach the base one, so this is how the name cache hears about a registration. A bare `EventRegistry.AggregateTypeFor` now throws `ArgumentOutOfRangeException` on an unknown alias rather than `NotSupportedException`.

  Until a store adopts these, it compiles with `CS0108` hiding warnings and behaves as before, because its own members win. That includes masking: a store's own rules hide the base ones, so adopt this before building on the shared masking in any later lift.

* **`IEventDatabase.WaitForNonStaleProjectionDataAsync` has a default implementation** ([#1046](https://github.com/JasperFx/jasperfx/issues/1046)).

  A store may delete its own and implement `RegisteredShardNames()`. A store that keeps its own is unaffected. A class that needs the method public should forward to `NonStaleProjectionWait.WaitAsync`, not to the interface, which would recurse. The default reads the ceiling once so sustained appends cannot keep it waiting, returns at once on an empty store, and always reports expiry as a `TimeoutException`.

* **`ProjectionTeardownPlan.For(source)` lists everything a rebuild must clear** ([#1046](https://github.com/JasperFx/jasperfx/issues/1046)).

  Implement `IProjectionTableResolver` to resolve it to tables. The SQL, the transaction and the tenant scoping stay in the store. The plan is the union of what the three stores each covered, and it honors `TeardownDataOnRebuild` across every data category. On a composite, `false` also covers its members, which is new: a member can still opt out on its own. `DeletePublishedTypesOnTeardown = false` is honored too, where Polecat and Fisher truncated those tables anyway.

* **`ProjectionBatchBase<TSession>`, `MessagePublishing`, and the shared `IMessageOutbox` and `IMessageBatch` are now in JasperFx.Events** ([#1045](https://github.com/JasperFx/jasperfx/issues/1045)).

  The interfaces live in `JasperFx.Events.Documents`. Polecat's and Fisher's own `IMessageOutbox` and `IMessageBatch` derive from them for one release, so existing implementers such as Wolverine's keep compiling. Move to the shared interfaces before those are removed. Pass the real `ShardExecutionMode` to the base constructor, which has no default, and always await `beforeCommit` as the last step before commit, even for an empty batch.

* **`EventDataMaskingBase<TSession>` and `StreamCompactingExecution<TSession>` are now in JasperFx.Events** ([#1048](https://github.com/JasperFx/jasperfx/issues/1048)).

  Implement `TryMask` as a call on your own event graph, so that this and the `EventRegistry` masking lift can be adopted in either order. The `*IncludingArchived*` reads have to include archived events, and `SupportsMaskingArchivedEvents` on the compliance fixture turns on the facts for them. Masking commits nothing on failure, and compaction queues its work on your session and returns whether it did, so a store that commits inside compaction can do so on `true`.

* **`Projections.StepThrough(toEvent, serializer)` replaces the stores' `RunProjectionAsync`, `RunProjectionByNameAsync` and `RunMultiStreamProjectionAsync` bodies** ([#1049](https://github.com/JasperFx/jasperfx/issues/1049)).

  `ProjectionStateSerializer` requires both a serialize and a deserialize delegate. There is deliberately no copy delegate, because an identity function would compile and record live references. Enroll `ProjectionStepThroughCompliance` when you adopt it.

* **Session hooks and enlistment** ([#1052](https://github.com/JasperFx/jasperfx/issues/1052)).

  `IDocumentSessionOperations.AddSaveListener`, `IDocumentWriteOperations.AddTransactionParticipant` and `ProjectionGraph.Subscribe(IEventSubscription)` throw `NotSupportedException` until a store adopts them, so a hook a store does not run fails at registration rather than losing messages silently. Use one `DocumentSessionHooks` per session, count `HasPendingParticipants` as work, and throw from either registration on a session whose commit the store does not run. A shared participant belongs to one unit of work, unlike the stores' own, which last until dispose.

  The hook order is fixed: before-save, participants, then the message hook immediately before commit. After-commit runs only after a successful commit, takes no cancellation token, and runs every hook before rethrowing a failure.

* **`session.MetadataContext` exposes session metadata** ([#1052](https://github.com/JasperFx/jasperfx/issues/1052)), including `CurrentUserName`, which is the same state as `LastModifiedBy` on all three stores. It is an accessor rather than `IDocumentSessionOperations : IMetadataContext`, because inheriting makes `session.TenantId` ambiguous (`CS0229`) on every store-typed session.

* **`IEventStore.IdentityTypeFor`** gives an aggregate's identity type, with strong types preserved ([#1052](https://github.com/JasperFx/jasperfx/issues/1052)). `IdentityTypeForProjectedType` answers differently per store, so use the new member. `IEventStore.StreamIdentity` is answered for you from the registry, and `IEventStore.FindNaturalKeyDefinition` is new.

* **`Insert<T>(T)`, `Update<T>(T)`, `StoreObjects` and `CheckExistsAsync<T>(object)` are on the shared write and read tiers** ([#1052](https://github.com/JasperFx/jasperfx/issues/1052)).

* **`AggregateWriteCacheSession` and `AggregateWriteBaseline<T>` hold the aggregate write-back** ([#1050](https://github.com/JasperFx/jasperfx/issues/1050)).

  `Flush` only after a commit you own, and call `Discard` otherwise, including on a failed save and on a transaction the caller owns. Register at the version the instance reflects, and use `Advance` only with the instance that actually holds the newer state. Call `TakeBaseline(..., 0)` for a missing stream, so a stale entry cannot outlive it.

* **`IAggregateWriteCache.StoreIfNewer` refuses a write older than the existing entry** ([#1050](https://github.com/JasperFx/jasperfx/issues/1050)). It is a default member, so custom caches keep compiling and get the guard through `TryTake` and `Store`. A write-back therefore reads as a take to anything counting takes. Override it if your cache can compare and swap, or if your counters matter.

* **`ScopedSubscriptionServiceWrapper<T, TOperations, TQuerySession, TSubscription>` is public** ([#1050](https://github.com/JasperFx/jasperfx/issues/1050)), so Marten and Fisher can drop their copies. Its scope now spans the store's whole run, including the commit, so a scoped service that flushes on dispose flushes after the commit.

* **`EventOperationsBase` holds the shared write surface** ([#1047](https://github.com/JasperFx/jasperfx/issues/1047)): `StartStream`, `Append`, `AppendOptimistic`, `AppendExclusive`, `FetchForWriting`, `FetchForExclusiveWriting`, `WriteToAggregate` and `WriteExclusivelyToAggregate`.

  Derive from it, or implement `IEventOperationsHost` if your class already has a base class, and implement the `*Core` seams. The base reads no tenant and assumes no append mode, so the tenant is stamped in your seams, on the instance the call was made on. Natural-key fetches stay per store.

* **`OpenTelemetryOptions` owns one `ActivitySource`, named like its `Meter`, and both are shared by name for the process** ([#1054](https://github.com/JasperFx/jasperfx/issues/1054)). Answer `IEventStore.Meter` and `IEventStore.ActivitySource` from your options rather than creating your own. Derive from `OpenTelemetryOptions<TChangeSet>` for `ExportCounterOnChangeSets`, and call `RecordCommit` from your commit listener.

* **`StoreActivator<TStore, TDatabase>` replaces a hand-written startup service** ([#1054](https://github.com/JasperFx/jasperfx/issues/1054)). Global locks stay inside Weasel's per-database apply: the activator takes none of its own. Under `ContinueOnFailures`, leave the Weasel database at `FailFast` and let the activator handle the failure, because a lost lock otherwise looks like a successful migration.

* **`StrongTypedIdResolver` replaces local strong-typed id discovery** ([#1054](https://github.com/JasperFx/jasperfx/issues/1054)). It keeps both stores' rules for class wrappers as named `StrongTypedIdStyle` values.

* **`JasperFx.Pagination` holds `CursorCodec`, `IPagedList<T>` and `PagedList<T>`** ([#1054](https://github.com/JasperFx/jasperfx/issues/1054)). The v1 cursor format is unchanged for clients, and the seek predicate stays per store.

* **`ClientSideAppendPlanner` holds the append decisions for client-side stores** ([#1051](https://github.com/JasperFx/jasperfx/issues/1051)), in the new `JasperFx.Events.Appending` namespace. Supply a row per action, scoped to that action's tenant, and call `Plan` and then `Apply`. `Plan` changes nothing and does no I/O, so a refused batch leaves every action as it was. A stream you forget to read arrives as `NotRead` and is refused rather than restarting at version 1. Marten keeps its own append path.

### Behavior changes

* **`Append(stream, expectedVersion, ...)` means the version of the stream after the append, on every store** ([#1047](https://github.com/JasperFx/jasperfx/issues/1047)).

  `Append(id, 2, e)` succeeds on a stream at version 1, as Marten and Polecat have always done. **This is a breaking change on Fisher and the in-memory store**, which read the argument as the version before the append, so the same portable call behaved differently per store:

  | Call | Fisher before 2.0 | Now |
  |---|---|---|
  | `Append(id, 2, e)`, stream at version 2 | Succeeds | Fails with a concurrency exception. Pass `3` |
  | `Append(id, 2, e)`, stream at version 1 | Fails | Succeeds |
  | `Append(id, 0, e)`, stream missing | Creates the stream | Throws `ArgumentOutOfRangeException`. Pass `1` |

  To migrate, add the number of events being appended to the version you were passing. `FetchForWriting` and `AppendOptimistic` are unaffected, because neither takes this argument. `FetchForWritingCompliance` pins the meaning, so a store on the old reading fails it.

* **Client-side appends are decided the same way on Polecat and Fisher** ([#1051](https://github.com/JasperFx/jasperfx/issues/1051)), as each store adopts the planner:

  * Polecat event timestamps come from the application's `TimeProvider` rather than SQL Server's clock, and a caller-set `IEvent.Timestamp` is kept. Inline projections and the stored row now agree.
  * Polecat no longer stamps correlation, causation, user or headers on the envelope when that `Enable*` option is off.
  * On Fisher, a session header no longer replaces an event's own header of the same name, and events raised by a projection are not checked against the slice's version.
  * `StartStream` with a non-zero `ExpectedVersionOnServer` is refused, and under conjoined tenancy so is an event of another tenant.

* **Two stores in one process with the same meter name share one `Meter`** ([#1054](https://github.com/JasperFx/jasperfx/issues/1054)). This applies as soon as you take JasperFx 3.0, with no store adoption, because `new OpenTelemetryOptions(name).Meter` is now shared by name. Don't dispose it.

* **With `ContinueOnFailures`, `IInitialData` no longer runs after a failed migration**, and a host stopping during activation cancels rather than logging a failed migration ([#1054](https://github.com/JasperFx/jasperfx/issues/1054)). This takes effect as each store adopts `StoreActivator`.

* **A malformed cursor is always refused with an `ArgumentException`**, and a key type the codec cannot carry now fails at `Encode` rather than on the next request ([#1054](https://github.com/JasperFx/jasperfx/issues/1054)). Polecat cursors over enum keys change bytes, and were undecodable before.

* **Stream validation is uniform on every event operations overload** ([#1047](https://github.com/JasperFx/jasperfx/issues/1047)), before anything is read, as each store adopts the base. A stream identity that doesn't match the store's `StreamIdentity` throws `InvalidOperationException`, an empty Guid or key throws `ArgumentOutOfRangeException`, `StartStream` with no events throws `EmptyEventStreamException`, and null events throw `ArgumentNullException`. In particular, `FetchForWriting(Guid.Empty)` no longer starts a `Guid.Empty` stream on Polecat or Fisher, and `AppendOptimistic(Guid.Empty)` throws `ArgumentOutOfRangeException` rather than `NonExistentStreamException`.

* **Pre-generated code fails at startup rather than at first use** ([#1041](https://github.com/JasperFx/jasperfx/issues/1041)). `DynamicCodeBuilder.LoadPrebuiltTypes()` now throws `ExpectedTypeMissingException` when the static type loader is in effect (`TypeLoadMode.Static`, or `Auto` on Native AOT) and an expected pre-built type is missing. Dynamic mode, `Auto` with runtime code generation, custom `ITypeLoader`s and the `codegen` commands are unaffected. The text of `ExpectedTypeMissingException` and `MissingTypeException` changed, so don't match on it.

* **Stepping through a projection shows true history** ([#1049](https://github.com/JasperFx/jasperfx/issues/1049)). A delete shows as a null state, and a failed apply no longer leaks partial changes into later steps. The typed `RunProjectionAsync` throws `ProjectionStateRoundTripException` for a state the serializer cannot read back faithfully, such as a `{ get; private set; }` property, instead of reporting wrong values; use `RunProjectionByNameAsync` for those aggregates. `ISteppableAggregation.BuildTimelinesAsync` stamps version metadata, honors `Compacted<T>`, treats `HardDelete` as a delete and rethrows cancellation, and those four reach stores without any adoption.

* **Masking and compaction fixes arrive as each store adopts the shared classes** ([#1048](https://github.com/JasperFx/jasperfx/issues/1048)):

  * Masking reaches archived streams and events. Marten and Polecat skipped them silently, so re-run masking if you relied on it for archived data.
  * An event selected twice in a pass is masked once, and a pass that fails commits nothing.
  * `ForTenant` refuses a null or empty tenant, and `IncludeStream` refuses `Guid.Empty` and an empty key.
  * Compaction leaves a stream that folds to nothing alone, where Marten and Polecat replaced it with an empty snapshot and deleted the events.
  * An archiver the store cannot call fails the compaction rather than being skipped, and compaction refuses an empty stream identity.

* **`StreamArchivingCompliance` holds every store to archived events being opt-in.** The suite used to let a store return archived events from `FetchStreamAsync`. It now asserts that `FetchStreamAsync`, `QueryRawEvents`, `QueryEventsAsync` and `QueryEventData<T>` leave them out, and that a predicate naming `IEvent.IsArchived` reads them, replacing the implicit filter rather than sitting beside it. A store whose `Where(x => x.IsArchived)` still carries its default `is_archived` term matches nothing and fails that fact.

* **Projection batches built on `ProjectionBatchBase` publish nothing outside `ShardExecutionMode.Continuous`** ([#1045](https://github.com/JasperFx/jasperfx/issues/1045)). A rebuild or catch-up replays history, so a message from one is a second delivery of something already sent, and a re-raised event permanently grows the stream on every rebuild. Neither the message hooks nor raised events were gated consistently across the stores. This takes effect in each store once it adopts the base.

* **`FetchProjectionLagAsync`:** a shard with only tenant-qualified progress rows, under a store-global high-water mark, now reports those tenants' cells rather than "no row" ([#1046](https://github.com/JasperFx/jasperfx/issues/1046)).

* **Masking rules against a base type now work** ([#1043](https://github.com/JasperFx/jasperfx/issues/1043)). `AddMaskingRuleForProtectedInformation<T>(Func<T,T>)` applies to events assignable to `T`, as the `Action<T>` overload always has. A replacing rule against a base type or interface used to match nothing, which left protected data in place, so re-run masking if you relied on one. The rule must return something the event type can hold, or the masking pass fails. This takes effect in each store once it adopts the shared masking.

* **Tag registration** ([#1043](https://github.com/JasperFx/jasperfx/issues/1043)): once Polecat and Fisher adopt it, an empty or whitespace table suffix is refused, and registering a tag type again with a different inner type throws.

### Compliance suites

* **`SingleTenantedEventSlicingCompliance` was removed** ([#1065](https://github.com/JasperFx/jasperfx/issues/1065)), along with `TallyIncremented` and `TenantSlicingTally`. It was marked obsolete in 2.x because it could only execute on Marten. Remove any enrollment from your store's compliance tests.

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

#### Convention-based projections are dispatched by the source generator

Conventional `Apply` / `Create` / `ShouldDelete` methods on a **projection subclass** — one that derives from `SingleStreamProjection<TDoc, TId>`, `MultiStreamProjection<TDoc, TId>`, or `EventProjection` — are now dispatched by the compile-time `JasperFx.Events.SourceGenerator`. Pre-2.0 they were dispatched by runtime reflection; **in 2.0 there is no runtime reflection fallback.** The convention methods must be `public` so the generated dispatcher can call them.

```csharp
public class TripProjection : SingleStreamProjection<Trip, Guid>
{
    public Trip Create(IEvent<TripStarted> e) => new() { Id = e.StreamId };
    public void Apply(Travel e, Trip trip) => trip.Traveled += e.TotalDistance();
}
```

**Most projections do not need to be `partial`.** The dispatcher for an aggregation projection is emitted as a separate, file-scoped type registered through `[assembly: GeneratedEvolver(...)]`, so it needs nothing from your declaration. This covers self-aggregating snapshot types registered via `Snapshot<T>` (or the single-arg `SingleStreamProjection<T>` / `AggregateStream<T>` forms) as well as `SingleStreamProjection<TDoc, TId>` / `MultiStreamProjection<TDoc, TId>` subclasses.

`partial` is required only where the dispatcher has to be generated **into the projection class itself**, which the generator reports as `JFXEVT003` at build time:

- an **`EventProjection`** subclass using conventional methods — its dispatcher is an `ApplyAsync` override on your class;
- a projection whose conventional methods are **instance** methods and which has **no public parameterless constructor** — a DI-activated projection, or an abstract projection base class. Dispatch has to run on the real instance so injected dependencies are populated;
- a **generic** projection, or one nested behind `private` / `protected`, which a separate type cannot name. Every containing type has to be `partial` too.

```csharp
// needs `partial`: dispatch must run on the DI-built instance
public partial class TripProjection(ILogger<TripProjection> logger) : SingleStreamProjection<Trip, Guid>
{
    public void Apply(Travel e, Trip trip) => logger.LogInformation("...");
}
```

When a projection needs `partial` and does not have it, the build fails with `JFXEVT003` naming the declaration that is missing the modifier. If the generator never ran at all — for instance because the project reference excludes the `analyzers` asset — the failure instead surfaces at projection registration:

```
JasperFx.Events.Projections.InvalidProjectionException:
No source-generated dispatcher found for TripProjection. Conventional
Apply/Create/ShouldDelete methods are dispatched by the compile-time
JasperFx.Events.SourceGenerator; there is no runtime fallback.
```

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
