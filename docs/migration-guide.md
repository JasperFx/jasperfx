# Migration Guide

## Key Changes in 3.13.0

* **Mapped and nullable natural keys** ([#1281](https://github.com/JasperFx/jasperfx/pull/1281), [marten#5813](https://github.com/JasperFx/marten/issues/5813)). An aggregate can map a natural key whose lookup another projection owns, with `[MappedNaturalKey<TSource>]` or `[NaturalKey(SourceProjection = typeof(TSource))]`, and a struct key can be declared as `TKey?` on the aggregate, a `[NaturalKeySource]` return type or an event property. An aggregate owns at most one key, maps any number, and every key on it has its own type. Stores support mapped keys separately: Marten 10 does. Polecat and Fisher accept the attribute but can't fetch by a mapped key yet ([polecat#1131](https://github.com/JasperFx/polecat/issues/1131), [fisher#679](https://github.com/JasperFx/fisher/issues/679)).

* **Natural key setups that used to fail silently are refused when projections are registered.** `InvalidProjectionException` for two owned keys on one aggregate, two keys of one type, an owned key that no event sets, a non-public `[NaturalKeySource]` method, two sources for one event that derive the key differently, an owned key on an aggregate inside a composite projection, and a mapped key whose source isn't registered, owns no key or owns a key of another type. An explicit `NaturalKeyFor()` mapping clears the source method refusals.

* **Build-time natural key diagnostics `JFXEVT010` to `JFXEVT017`**, all errors, from the source generator in `JasperFx.Events`. They cover the rules above, and a key type named by Wolverine's `[WriteAggregate<TKey>]`, `[ReadAggregate<TKey>]`, `[WriteModel<TKey>]`, `[ReadModel<TKey>]` or `NaturalKey = typeof(TKey)` that the aggregate doesn't have, or that is `TKey?`.

* **New store members for event store authors, all with defaults**, so a store built against 3.12 still compiles and loads:
  * `IEventStoreOperations.FetchForWriting<T, TId>(id, expectedVersion)` fetches, then checks the version. A store should override it to check before the stream is enlisted in the session.
  * `IEventStoreOperations.FetchManyForWriting<T, TId>(ids)` fetches one id at a time, after `IEventStoreOperations.AssertFetchableIds` refuses a `Nullable<T>` id type, a null id and a repeated id.
  * `IAggregateProjection.MappedNaturalKeyDefinitions` is empty.
  * `AggregateWriteCacheSession.OnPublished(key, aggregate)` calls the existing `OnPublished(key)`, and an override that throws no longer fails a commit that has already happened.
  * `NaturalKeyDefinition` gains `SourceProjectionType`, `IsMapped`, `SourceProjectionDefinition`, `LookupAggregateType`, `IsNullable`, `KeyTypeOf()` and `ReadValue()`, and `ProjectionGraph.AllAggregateProjections()` includes composite members.
  * Compliance suites `MappedNaturalKeyCompliance` (behind the new `SupportsMappedNaturalKeys`) and `NaturalKeyRegistrationCompliance`.

* **A store's aggregate types, and stream counts and lengths by aggregate type** ([#1282](https://github.com/JasperFx/jasperfx/issues/1282)). `IEventStore.RegisteredAggregateTypes()` and `IEventDatabase.FetchAggregateStreamStatsAsync(AggregateStreamStatsRequest)`. Both default to throwing `NotSupportedException`; the in-memory store implements them, and `AggregateStreamStatsCompliance` (behind `SupportsAggregateStreamStats`) is the contract for the stores.

## Key Changes in 3.12.1

* **A composite projection rebuild that fails now throws** ([#1277](https://github.com/JasperFx/jasperfx/issues/1277)). Rebuilding a composite projection used to return normally when a page failed and the shard ended up paused or stopped, so the rebuild looked finished when it wasn't. It now throws the shard's own failure, the same way a single-stream rebuild always has. A composite rebuild that runs out of time between pages throws `OperationCanceledException`. A full rebuild never parks identities, so `RebuildErrors = Park` behaves like Pause there and throws too.

## Key Changes in 3.12.0

* **JasperFx now depends on the .NET 11 GA packages** ([#1270](https://github.com/JasperFx/jasperfx/issues/1270)). The net11.0 build takes `Microsoft.Extensions.*` and `Microsoft.Bcl.TimeProvider` 11.0.0 instead of 11.0.0-rc.3, so no .NET 11 prerelease package comes in through JasperFx any more.

* **A natural key of a type a store can't hold is refused when the projection is registered** ([#1272](https://github.com/JasperFx/jasperfx/issues/1272)). A natural key must be an `int`, `long` or `string`, or a strong-typed identifier wrapping one of those. Anything else, a `Guid` for example, used to pass validation and then fail on the first append. It now throws `InvalidProjectionException` at startup, naming the aggregate, the member and its type.

* **A parked identity can carry its natural key** ([#1273](https://github.com/JasperFx/jasperfx/issues/1273)). `ParkedIdentity` has a new `NaturalKey` property that a store fills when it reads the parked list, and `projections parked` shows it in a new "Natural Key" column after "Identity". `--identity` still takes the stream id. See [Parking a Failing Identity](/events/parked-identities).

* **The extended progression write contract is restated** ([#1270](https://github.com/JasperFx/jasperfx/issues/1270)). A state carrying a failure creates the shard's progression row at its start position when there isn't one yet; every other state only updates an existing row. This is documentation for store authors and doesn't change JasperFx's behaviour.

## Key Changes in 3.11.0

* **Transient failures while building a projection batch are retried through the store's resilience pipeline** ([#1256](https://github.com/JasperFx/jasperfx/issues/1256)). A batch that fails for a transient reason (a timeout, a dropped connection, a driver's transient error) is built again through the store's existing Polly pipeline, under every error policy, whether one slice failed or several. When the pipeline gives up, the shard pauses with `IsTransient = true` so a supervisor such as Wolverine can restart it. Skip no longer retries such a batch without limit. `IEventStore` has a new `ResiliencePipeline` member that defaults to `ResiliencePipeline.Empty`; with the default, a transient failure pauses the shard the first time.

* **Replaying or discarding a parked identity right after it parks** no longer answers `NotParked` ([#1258](https://github.com/JasperFx/jasperfx/issues/1258)).

* **The extended progression row keeps the parked count current** with periodic heartbeats off ([#1255](https://github.com/JasperFx/jasperfx/issues/1255)).

* **Stepping a projection from stored events keeps their DCB tags** ([#1254](https://github.com/JasperFx/jasperfx/issues/1254)). A new `WithRecordMetadata(..., tagTypes)` overload rebuilds an event's tags from `EventRecord.Tags`, whose keys are the tag type's name (a table suffix is accepted too). Events created by fan-out or enrichment while stepping carry their source event's tags.

## Key Changes in 3.10.0

* **Parking a failing projection identity** ([marten#6003](https://github.com/JasperFx/marten/issues/6003), [#1251](https://github.com/JasperFx/jasperfx/pull/1251), [#1252](https://github.com/JasperFx/jasperfx/pull/1252)). An async projection can now set aside the one identity whose events fail to apply, keep running for every other identity, and replay or discard the parked one later. Nothing parks unless a store or your configuration asks for it, so JasperFx's own default does not change. See [Parking a Failing Identity](/events/parked-identities).

* **Rebuilding one identity of a multi-stream projection.** `RebuildIdentityAsync` now works for a multi-stream projection that groups by stream (`IdentityFromStream<TEvent>()`), by tag (`IdentityFromTag<TTag>()`) or through the new slice index (`UseSliceIndex()`). One whose grouping cannot find a single identity's events is now refused with `IdentityRebuildRefusal.IdentityNotFindable` instead of `NotASingleStreamProjection`; `RebuildIdentityByScanAsync` rebuilds those by reading the event store.

* **New store members for event store authors.** `SupportsIdentityEventSource`, `LoadEventsForIdentityAsync`, `IProjectionBatch.AppendSliceIndex` and `IEventDatabase.PruneSliceIndexAsync`, plus the parked identity members from #1251. All have defaults, so a store built against 3.9.0 still compiles and simply does not park.

## Key Changes in 3.9.0

* **Culture-independent names** ([#1246](https://github.com/JasperFx/jasperfx/pull/1246), [#1248](https://github.com/JasperFx/jasperfx/issues/1248)). `EqualsIgnoreCase`, `StartsWithIgnoreCase` and `EndsWithIgnoreCase` now compare with `StringComparison.OrdinalIgnoreCase` instead of the current culture, and `ToTableAlias` lowers with the invariant culture. On most machines nothing changes. On a machine whose culture is Turkish or Azerbaijani, an uppercase `I` used to become a dotless `ı`. Event type names, tag table suffixes and duplicated-field column names created there with an `I` in the type or member name will now be spelled differently. If you ran on such a machine, map the old names with an event type alias before upgrading.

* **Command and flag names are matched the same way in every culture** ([#1248](https://github.com/JasperFx/jasperfx/issues/1248)). Command names, flag names and aliases are lower-cased and compared with the invariant culture, both at runtime and in the code the CLI source generator writes. On a Turkish machine, a command or flag with an `I` in its name used to miss.

* **More of an `Evolve` body counts toward the events an aggregate fetches** ([#1243](https://github.com/JasperFx/jasperfx/issues/1243)). The generator used to read only declaration patterns (`case Foo foo:`) when it built the evolver's `EventTypes` filter. A bare `case Foo:`, a `Foo =>` switch arm, `case Foo when ...:` and the types inside an `or` pattern now count too, and `not Foo` is still skipped. Events matched any of those ways were never fetched before, so an aggregate written that way can start seeing events it was always meant to handle. See [Projection Apply Dispatch](/codegen/projection-apply-dispatch).

## Key Changes in 3.8.0

* **Command line numbers and time spans are read with the invariant culture** ([#1237](https://github.com/JasperFx/jasperfx/issues/1237)). Number flags and arguments, `TimeSpan` quantities, and the `TODAY+N` and `HH:mm` forms of a date used the machine's culture. On a German machine `1.5` read as 15, on a French machine `-1.5` was split into three short flags, and on an Arabic machine `-5` threw. The decimal separator is now always `.`. A full date (`DateTime.Parse`) still uses the machine's culture. See [Arguments and Flags](/cli/arguments-flags).

* **An `Evolve` aggregate can inherit its conventional methods** ([#1241](https://github.com/JasperFx/jasperfx/issues/1241)). When a self-aggregating type with an `Evolve` method inherits public `Apply`, `Create` or `ShouldDelete` methods from a base class, the generated evolver now routes those event types to the inherited methods first, and they join the aggregate's `EventTypes`. Before, those methods were never called.

## Key Changes in 3.7.1

* **Stopping the daemon while a shard is loading a page no longer fails it** ([#1231](https://github.com/JasperFx/jasperfx/issues/1231)). Stopping an agent now waits for its in-flight page load, and a page that arrives after the stop is dropped. This showed up as "The channel has been closed" failing a catch-up that had actually worked.

* **A rebuild reads the running agents after it takes the registry lock** ([#1233](https://github.com/JasperFx/jasperfx/issues/1233)), so it can't miss an agent that started in between.

## Key Changes in 3.7.0

* **Backfilling a new projection version from where the old one stopped** ([#1222](https://github.com/JasperFx/jasperfx/issues/1222)). `AsyncOptions.SubscribeFromPriorVersionMark(backfillName)` starts a new version of a projection where its prior version left off, at a mark stored for the named backfill. The backfill job stops at the same mark, so the live version and the backfill split history at exactly one sequence and nothing is processed twice. The jobs that run backfills live in Wolverine. A store that doesn't implement event-range marks fails the shard at startup with `NotSupportedException`, rather than replaying history the backfill also covers.

* **Rewriting stored events** ([#1215](https://github.com/JasperFx/jasperfx/issues/1215)). `EventRegistry.AddRewriteTransform<T>(name, transform)` registers a named transform that a rewrite can apply to stored events, and naming one that isn't registered throws `UnknownRewriteTransformException`. This rewrites history: nothing derived from the old events, like projections, natural keys or tags, is updated.

* **New store members for event store authors, all with defaults**: `IEventStore.SupportsEventRewrites` and `RewriteStoredEventsAsync`, `IEventStore.SupportsBackfills` and `RunBackfillAsync`, and `IEventDatabase.FetchEventRangeMarkAsync`, `TrySetEventRangeMarkAsync` and `SetEventRangeMarkAsync`. The contracts are `EventRangeMarkCompliance`, `EventRewriteCompliance` and `BackfillCompliance`.

* **Stopping the daemon saves the `Stopped` status** ([#1225](https://github.com/JasperFx/jasperfx/issues/1225)). With extended progression on, `StopAllAsync()` followed straight away by `Dispose()` left the shard's progression row saying `Running`. Both `StopAllAsync()` and `StopAgentAsync()` now wait for the status to be written, bounded by `DaemonSettings.StopAndDrainTimeout`.

* **F# output leaves the AOT roots companion out** ([#1228](https://github.com/JasperFx/jasperfx/issues/1228)), even for an assembly assembled on the C# default and rendered with `GenerateFSharpCode()`. See [Publishing AOT](/codegen/aot).

## Key Changes in 3.6.0

* **New time-ordered Guids, `SequentialGuid`.** `JasperFx.Core.SequentialGuid` makes Guids in MassTransit's NewId layout (a 100 ns timestamp, a 6-byte worker id drawn at random once per process, and a sequence) without a lock and without a random number per id. There are two sort orders, one per database: `SequentialGuid.NextPostgresOrder()` for PostgreSQL `uuid` (and SQLite, or anything that stores the id as text) and `SequentialGuid.NextSqlServerOrder()` for SQL Server `uniqueidentifier`. `SequentialGuid.PostgresOrder` and `SequentialGuid.SqlServerOrder` are the same thing as an `IGuidGenerator` to hand to something else.

* **Appended event ids, new stream ids and dead letter ids now come from `SequentialGuid.NextPostgresOrder()`.** Event and stream ids were COMB Guids, and `DeadLetterEvent` ids were `Guid.CreateVersion7()`. Both of those are only ordered by millisecond and random within it, and both draw from the OS random number generator on every id.

* **A store chooses the generator for the Guid stream ids JasperFx makes up** ([#1220](https://github.com/JasperFx/jasperfx/issues/1220)). `IEventRegistry.StreamIdGenerator` (settable on `EventRegistry`) and the protected virtual `EventOperationsBase.StreamIdGenerator` both default to `SequentialGuid.PostgresOrder`. They are used by `StartStream` without an id and by the new stream an `EventBoundary` starts for an event no tag routes. A SQL Server store such as Polecat sets it to `SequentialGuid.SqlServerOrder`, because its stream table is keyed by a clustered `uniqueidentifier`. Both members have defaults, so nothing changes for a store that does not set them.

* **`CombGuidIdGeneration.NewGuid()` delegates to `SequentialGuid.NextPostgresOrder()`.** It keeps its signature and stays `[Obsolete]`, now pointing at `SequentialGuid`. `NewGuid(DateTimeOffset)`, `Create()` and `GetTimestamp()` still make and read COMB Guids, but `GetTimestamp()` cannot read an id from `NewGuid()` any more.

* **New ids do not sort like version 7 or COMB Guids.** They start with the .NET tick count, not unix milliseconds. In PostgreSQL and SQLite every new id sorts after every existing version 7 or COMB id, so an index keeps appending at its right edge and there is nothing to migrate. Code that read a timestamp back out of these ids (`Guid.Version == 7`, `CombGuidIdGeneration.GetTimestamp()`) has to stop doing that.

* **`IStorageOperations.LastAppendedPosition` replaces `LastAppendedSequence`** ([#1218](https://github.com/JasperFx/jasperfx/issues/1218)). The 3.5.0 name holds a full `EventStorePosition` (database, tenant and sequence), not a sequence. `LastAppendedSequence` stays as an `[Obsolete]` forwarder to `LastAppendedPosition`. A store built against 3.5.0 that implements the old name keeps working for callers of the old name, but answers null under the new one until it moves over.

* **A paused projection on another node shows as `Paused` to `WaitForProjectionsAsync()` only with extended progression on.** Without it the wait runs to its timeout and returns `TimedOut`, which never fails the write.

* **Rebuild one identity of a single-stream projection inside its running shard** ([#1201](https://github.com/JasperFx/jasperfx/issues/1201)). `IProjectionDaemon.RebuildIdentityAsync(projectionName, tenantId, identity)` binds the rebuild to the next batch the shard builds, re-folds that identity over its stream with side effects suppressed, and commits it with the shard's progression. A rebuild that can't run is refused before anything is written with `IdentityRebuildRefusedException`. Store authors get default members for `SupportsIdentityRebuild`, `LoadEventsForIdentityRebuildAsync` and `IProjectionStorage.HardDeleteById`, plus `IdentityRebuildGuard` and the `IdentityRebuildCompliance` suite. See [Parking a Failing Identity](/events/parked-identities#rebuilding-one-identity).

* **`projections --advance` works again** ([#1210](https://github.com/JasperFx/jasperfx/issues/1210)). It advances every selected database's high water mark before a run or rebuild and reports what each mark did, and a failed advance stops before anything runs. A rebuild now covers every selected event store instead of returning after the first.

* **`StreamState.ColdVersion`** ([#1211](https://github.com/JasperFx/jasperfx/issues/1211)) is the last version of a stream in an application-owned cold store, when there is one.

* **A slice that ends with its `Compacted<T>` marker hands the marker to `StoreProjection` as the last event** ([#1214](https://github.com/JasperFx/jasperfx/issues/1214)) instead of null.

## Key Changes in 3.5.0

* **Read your own writes from an async projection** ([marten#6004](https://github.com/JasperFx/marten/issues/6004)). After you commit, wait for the projections you care about to catch up to your events, and only your events:

  ```cs
  var position = session.LastAppendedPosition;
  var result = await store.WaitForProjectionsAsync(position, [typeof(TripSummary)], TimeSpan.FromSeconds(5));
  ```

  `EventStorePosition` is the database, tenant and sequence of what a session committed. It's small, serializes to JSON, and has a compact text form (`ToString()`, `Parse()`, `TryParse()`) to carry in a header from one request to the next. `WaitForProjectionsAsync` takes projection types (or the documents they write) or names, and returns a `ProjectionWaitResult` that says `Reached`, `TimedOut`, `Paused` or `Rebuilding`, with how far each shard got. It never throws on a timeout, because a slow projection shouldn't fail a write that already succeeded. It reads the progression table, so it works from any node. `IEventStore.AsyncShardNamesFor(Type)` lists the shards for a projection type. 3.6.0 renamed the session member to `LastAppendedPosition`.

* **`projections floor`** ([#1205](https://github.com/JasperFx/jasperfx/issues/1205)) shows, pins, sets and clears a projection's start floor, for any store that implements the floor seam:

  ```bash
  dotnet run -- projections floor --projection Trips
  dotnet run -- projections floor --projection Trips --pin-current
  dotnet run -- projections floor --projection Trips --set 12345
  dotnet run -- projections floor --projection Trips --clear
  ```

  Showing is the default. A write needs one projection, applies to every selected database, and asks first unless you pass `--yes`.

* **`ForTenant` on the shared document write operations** ([#1202](https://github.com/JasperFx/jasperfx/issues/1202)). `IDocumentWriteOperations.ForTenant(tenantId)` lets store-agnostic code `Insert`, `Update` and `Store` as another tenant without casting to a store's session. A store that hasn't adopted it throws rather than writing to the wrong tenant.

* **A store-wide projection lag read.** `IEventStore.FetchProjectionLagAsync(token)` reads the lag of every shard across every database the store has, composite members included. The `ShardName` overload narrows it to one shard.

* **Warning `JFXEVT009` for a non-public conventional handler** ([#1196](https://github.com/JasperFx/jasperfx/issues/1196)). Generated dispatch only calls public `Apply`, `Create` and `ShouldDelete` methods, so a non-public one compiled, ran and never applied its event. See [Build Diagnostics](/codegen/diagnostics).

* **A warning when a composite member sets a shard setting the composite ignores.** A composite runs as one shard built from its own options, so `BatchSize`, `MaximumHopperSize`, `EnableDocumentTrackingByIdentity`, `GateSideEffectsBehindPriorVersion` and the starting position on a member are never read. The daemon now logs which ones once per composite.

* **`JasperFxOptions.RememberApplicationAssembly`** ([#1204](https://github.com/JasperFx/jasperfx/issues/1204)) lets a host opt out of recording and reusing the process-wide remembered application assembly, and `JasperFxOptions.ResetRememberedApplicationAssembly()` clears it between hosts in a test suite. See [JasperFxOptions](/configuration/jasperfx-options).

* **Event modeling: which stream an emitted event goes to** ([#1203](https://github.com/JasperFx/jasperfx/issues/1203)), with `.Emits<T>().OnStream<TAggregate>()`. See [Declaring the Model in Code](/event-modeling/declaring#which-stream-an-event-goes-to).

## Key Changes in 3.4.0

* **No more broken builds from an aggregate a generated evolver can't name** ([#1194](https://github.com/JasperFx/jasperfx/issues/1194)). For an open generic aggregate, or one nested behind `private` or `protected`, the generator used to emit an evolver that didn't compile, inside a generated file you can't edit. It now emits nothing for it and reports warning `JFXEVT008`, unless something else in the compilation already covers the type. See [Build Diagnostics](/codegen/diagnostics).

## Key Changes in 3.3.0

* **A `[FromKeyedServices]` key that isn't a string now resolves its own registration** ([#1189](https://github.com/JasperFx/jasperfx/issues/1189)). Codegen turned the key into a string before looking it up, so an enum key never matched and silently fell through to the last unkeyed registration of the type. Keys now match the way Microsoft's container matches them. **Watch for:** a keyed parameter whose key has no registration now fails codegen instead of quietly taking the unkeyed service.

* **An aggregate's non-public parameterless constructor now runs** ([#1188](https://github.com/JasperFx/jasperfx/issues/1188)). An aggregate whose only parameterless constructor was private, protected or internal was created without calling it, so the constructor body and every field and property initializer were skipped, and the first `Apply` could throw a `NullReferenceException`. The generated evolver now calls that constructor through `[UnsafeAccessor]`, which stays trim and AOT safe. If you were relying on initializers *not* running, they run now.

* **`IEventStoreOperations.UnArchiveStream(Guid)` and `UnArchiveStream(string)`** ([marten#5476](https://github.com/JasperFx/marten/issues/5476)), so store-agnostic code can do what `ArchivedStreamException` tells it to. The compliance fixture's `UnArchiveStream` hook is obsolete.

* **`IEventBinarySerializer` can write into pooled buffers** ([#1187](https://github.com/JasperFx/jasperfx/issues/1187)). `Serialize(Type, object, IBufferWriter<byte>)` and `Deserialize(Type, ReadOnlySpan<byte>)` are default members over the existing `byte[]` pair, so a serializer built against 3.2 keeps working.

## Key Changes in 3.2.0

* **A `[BoundaryAggregate]` with an `Id` member also gets a string-keyed evolver** ([#1184](https://github.com/JasperFx/jasperfx/issues/1184)). Marten builds every boundary aggregate's projection keyed by string, so `FetchForWritingByTags<T>()` found no dispatcher for a boundary aggregate with, say, a `Guid Id`.

## Key Changes in 3.1.0

* **Runtime-async is on by default for `net11.0`** ([#1180](https://github.com/JasperFx/jasperfx/issues/1180)). Both switches described under [3.0.0](#foundation) flip:

  * **Generated code:** `GenerationRules.UseRuntimeAsync` now defaults to `true` in the `net11.0` build (it stays `false` in the `net10.0` build, where the runtime cannot load runtime-async code). Runtime-compiled code then uses runtime-async methods instead of compiler-built state machines. Opt out with `GenerationRules.UseRuntimeAsync = false` (Wolverine exposes it as `opts.CodeGeneration.UseRuntimeAsync`).
  * **JasperFx's own assemblies:** the `net11.0` packages are compiled with `runtime-async=on`. Build with `-p:JasperFxRuntimeAsync=false` to compile them without it.

  Measured in Wolverine's in-process benchmark ([wolverine#5542](https://github.com/JasperFx/wolverine/issues/5542)), allocations fell by 38–84% and median latency by 5–34%, depending on how often a handler suspends. Plain `net11.0` without the feature changed nothing. **Watch for:** stack traces, debugger stepping, and APM agents or profilers that hook async state machines can behave differently with runtime-async methods. If one misbehaves, turn the generated-code switch off first.

* **`StreamLockedException.StreamIds`** ([#1172](https://github.com/JasperFx/jasperfx/issues/1172)): a store whose lock covers several streams at once can report every stream that lost it. The single-stream constructor and `Id` are unchanged.

* **`--log-level` and `--verbose` win over configured log levels** ([#1132](https://github.com/JasperFx/jasperfx/issues/1132)). Before, a `Logging:LogLevel` rule bound from configuration silently overrode the flag.

* **Extensions that defer `AddJasperFx()` can hand over the registering assembly** ([#1179](https://github.com/JasperFx/jasperfx/issues/1179)). The new overload `AddJasperFx(services, configure, registeredFrom)` uses the assembly the entry point captured before it deferred (for example from an `IHostBuilder.ConfigureServices` callback that runs inside `Build()`), instead of walking the stack inside `Build()`. Without it, a host built by a helper in another assembly was attributed to that helper and `ApplicationAssemblyReuseWarning` fired on a healthy host. Nothing changes for callers of the existing overloads.

## Key Changes in 3.0.1

* **Projection side effects publish under Native AOT** ([#1171](https://github.com/JasperFx/jasperfx/issues/1171)). Publishing a message from a projection batch compiled a delegate with FastExpressionCompiler, which Native AOT refuses, so every side effect failed and paused its shard. It now falls back to the expression interpreter where dynamic code isn't available.

## Key Changes in 3.0.0

JasperFx 3.0 is the foundation of the Critter Stack .NET 11 release train, alongside Weasel 10, Marten 10, Polecat 6, Fisher 2 and Wolverine 7.

::: tip
This section covers 3.0.0 itself. Everything that changed in a later 3.x release is in that release's section above, newest first.
:::

### Foundation

* **.NET 9 support was dropped.** JasperFx 3.0 targets `net10.0;net11.0` ([#1039](https://github.com/JasperFx/jasperfx/issues/1039)). Stay on JasperFx 2.x if you still need .NET 9. The repository builds with C# 14 (`LangVersion` 14), and its workflows install the 11.0.x SDK beside 10.0.x. The source generator tests run on `net10.0`.

* **Runtime-async is opt-in, in two places** ([#1040](https://github.com/JasperFx/jasperfx/issues/1040)). It only takes effect on `net11.0`, running on a .NET 11 runtime with Roslyn 5.x. On `net10.0` it is ignored.

  * **Generated code:** set `GenerationRules.UseRuntimeAsync = true` (default `false`) to compile runtime-generated code with the C# compiler's `runtime-async=on` feature, so async methods are emitted as runtime-async methods rather than compiler-built state machines. It is part of the reuse key for `ReuseCompiledAssemblies`, so a runtime-async and a plain compilation of the same source never share an assembly. `AssemblyGenerator.UseRuntimeAsync` is the same switch when you drive the generator directly.
  * **JasperFx's own assemblies:** build with `-p:JasperFxRuntimeAsync=true` to compile the `net11.0` assemblies with the feature. Changing the property does not trigger a recompile, so clean `bin/` and `obj/` between a build with it and a build without.

  Nothing changes unless you opt in. **3.1.0 turns both on by default for `net11.0`**, see [Key Changes in 3.1.0](#key-changes-in-3-1-0).

* **`JasperFx.RuntimeCompiler` moves to 6.0.0**, because it follows the same target framework change and depends on JasperFx 3.

* **`Microsoft.Extensions.*` packages have a 10.0.0 floor.**

* **JasperFx.Aspire targets `net10.0;net11.0` and Aspire 13.6** ([#1055](https://github.com/JasperFx/jasperfx/issues/1055)). The `Aspire.Hosting` packages move from 13.4.6 to 13.6.4 (13.6.5 since 3.6.0, for [GHSA-f7vh-h5g3-7jrh](https://github.com/advisories/GHSA-f7vh-h5g3-7jrh)), so an AppHost that references JasperFx.Aspire moves with them.

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

### Exceptions

* **`StreamLockedException` derives from `ConcurrencyException`** ([#1169](https://github.com/JasperFx/jasperfx/issues/1169)). A stream lock timeout is the same user-facing failure as an optimistic concurrency conflict (a 409, retry), so it now shares the type. Code that does `catch (ConcurrencyException)` and retries also catches lock timeouts, which is usually what is wanted. Code that caught `ConcurrencyException` and treated it as a version mismatch only should catch `StreamLockedException` first. `ConcurrencyException.Id` carries the stream id, and `DocType` is null.

### C# 15 unions as events (`net11.0`)

* **A C# 15 union can be appended and applied as an event** ([#1053](https://github.com/JasperFx/jasperfx/issues/1053)). Everything union-specific is `net11.0` only; on `net10.0` no type is a union.

  * **Append stores the active case.** Each case gets its own event type name and `IEvent<TCase>`, and the stored JSON is unchanged because System.Text.Json already writes the bare case. `AddEventType(union)` registers every case.
  * **`Apply(TUnion)` is dispatched per case.** A handler that takes a union listens for the union's cases, and the source generator emits one arm per case that rebuilds the union. An explicit `Apply(TCase)` wins over `Apply(TUnion)`.
  * **Refused shapes:** a union in `Create`, `ShouldDelete`, `Project`, a constructor, or `IEvent<TUnion>` fails validation, rather than listening for the cases and dispatching none of them.
  * **`UnionTypes`** (`IsUnion`, `CaseTypes`, `ExpandToCaseTypes`, `UnionCasesFor`, `Unwrap`, `Wrap`) is public for stores and tools.
  * **Store authors:** an `EventGraph.BuildEvent` override that does not end in `base.BuildEvent` has to call `UnionTypes.Unwrap` itself.

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

A non-public method is not an error. Under reflection it was called, and now it is skipped, so the projection compiles and runs but never applies that event. The generator reports each one as warning `JFXEVT009` ([#1196](https://github.com/JasperFx/jasperfx/issues/1196)). The warning covers `Apply` / `Create` / `ShouldDelete` methods, `Project` / `Create` / `Transform` on an `EventProjection`, and single-argument event constructors on a self-aggregating type. It fires only when the member has a parameter shape the generator would dispatch if it were public. Make the member `public`. If it is a private helper that only happens to share the name, rename it or mark it `[JasperFxIgnore]`. On a type with no public handlers at all, the warning fires only when something in the same compilation names the type as an aggregate: a projection over it, a `Snapshot<T>`-style call, an `IRefersToAggregate` parameter, or `[BoundaryAggregate]`.

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
- a **generic** projection, or one nested behind `private` / `protected`, which a separate type cannot name. Every containing type has to be `partial` too.

In 2.0 a DI-activated projection, one whose conventional methods are instance methods and which has no public parameterless constructor, needed `partial` too. Since 2.48.0 ([#653](https://github.com/JasperFx/jasperfx/issues/653)) the generated evolver is handed the registered projection instance through its constructor, so it doesn't:

```csharp
// no `partial` needed: the evolver calls Apply on the DI-built instance
public class TripProjection(ILogger<TripProjection> logger) : SingleStreamProjection<Trip, Guid>
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
