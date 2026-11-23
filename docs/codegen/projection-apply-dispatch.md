# Projection Apply Dispatch

When you write conventional methods on an aggregate or a projection, something has to turn "an event of type `X` arrived" into a call to your `Apply(X, ...)`. In the Critter Stack, that something is generated C#, written at build time by the source generator that ships inside the `JasperFx.Events` package.

```csharp
public class Trip
{
    public Guid Id { get; set; }
    public double Traveled { get; set; }

    public static Trip Create(TripStarted e) => new() { Id = e.TripId };
    public void Apply(Travel e) => Traveled += e.Distance;
    public bool ShouldDelete(TripAborted e) => true;
}
```

The generator reads that type and emits an evolver: a `switch` over the event types that calls `Create`, `Apply` and `ShouldDelete` directly, with no reflection and no expression compilation at runtime. That's the only dispatch path. There is no runtime fallback, so if no generated dispatcher exists for a projection that needs one, the projection is refused when it's registered with `InvalidProjectionException: No source-generated dispatcher found`.

::: info
Before JasperFx 2.0, conventional methods were found by reflection when a projection was registered and compiled into delegates with FastExpressionCompiler. That path was deleted in 2.0 ([#276](https://github.com/JasperFx/jasperfx/issues/276)), along with the inline-lambda registration APIs (`CreateEvent`, `ProjectEvent`, `Project<TEvent>(handler)` and friends) that only it could serve. If you're coming from Marten 8 or earlier, see the [migration guide](/migration-guide).
:::

## What the generator emits

| Your code | What's generated | How the runtime finds it |
|---|---|---|
| A self-aggregating type with `Apply` / `Create` / `ShouldDelete` methods, used through `Snapshot<T>()`, `AggregateStreamAsync<T>()`, a single-stream projection of `T` and the like | A separate, `file`-scoped evolver class implementing `IGeneratedSyncEvolver<TDoc, TId>`, or `IGeneratedSyncDetermineAction<TDoc, TId>` when there's a `ShouldDelete`, registered with an assembly-level `[GeneratedEvolver]` | The projection base class looks up the `[GeneratedEvolver]` registrations in the aggregate's assembly, and the projection's when that differs, and binds the evolver |
| A self-aggregating type with its own `Evolve` method | The same kind of evolver, built around your `Evolve`, implementing `IGeneratedSyncEvolver` or `IGeneratedAsyncEvolver` | The same |
| A `SingleStreamProjection` or `MultiStreamProjection` subclass with conventional methods | A separate evolver registered with `[GeneratedEvolver]`. When the methods are instance methods, the evolver takes the registered projection instance through its constructor, so a projection built by your IoC container keeps its dependencies | The same, passing the projection instance to the evolver |
| An `EventProjection` with `Project`, `Create` or `Transform` methods | An `ApplyAsync` override written into your class, which is why the class has to be `partial` | Ordinary virtual dispatch |
| An `EventProjection` that writes documents from `ApplyAsync` | A `PublishedTypes()` override that registers each document type it writes, so schema creation and rebuild teardown know about them | Read when the projection is registered |

A projection that the separate evolver can't name, because it's generic or nested behind `private` or `protected`, gets its dispatcher written into the class itself instead, as an `Evolve`, `EvolveAsync` or `DetermineActionAsync` override. That's the other case that needs `partial`, on the class and on every type it's nested in.

If you'd rather write the dispatch yourself, override `Evolve`, `EvolveAsync`, `DetermineAction` or `DetermineActionAsync` (or `ApplyAsync` on an `EventProjection`). Then there's nothing to generate, and no `partial` is needed.

### When `partial` is required

Most projections do **not** need to be `partial`. Only these do:

- an `EventProjection` with conventional methods, because its dispatcher is an override on your class;
- a generic projection, or one nested behind `private` or `protected`, along with every containing type.

The build reports `JFXEVT003` when one of those is missing the modifier. A DI-activated projection hasn't needed `partial` since 2.48.0 ([#653](https://github.com/JasperFx/jasperfx/issues/653)). See [Build Diagnostics](/codegen/diagnostics) for every diagnostic the generator reports.

### Only public members are called

Generated code calls public `Apply`, `Create`, `ShouldDelete`, `Project` and `Transform` methods, and public single-argument event constructors. A non-public one is skipped, which reflection-based dispatch never did, so the build warns with `JFXEVT009`. Make it public, or mark a helper that only shares the name with `[JasperFxIgnore]`.

An aggregate's parameterless constructor is the exception. If it's private, protected or internal, the evolver calls it through `[UnsafeAccessor]` (since 3.3.0), so its body and your field initializers run, and the call stays trim and AOT safe. Only an aggregate with no parameterless constructor at all, or a generic or private-nested one the generated file can't name, is created without running one.

### Inherited methods

When a self-aggregating type with an `Evolve` method inherits public `Apply`, `Create` or `ShouldDelete` methods from a base class, those event types go to the inherited methods first (and to `Evolve` as well when its body names the type, a base type or interface of it, or no type at all), and everything else goes to `Evolve` (since 3.8.0, [#1241](https://github.com/JasperFx/jasperfx/issues/1241)).

## Which events an aggregate fetches

Every evolver carries an `EventTypes` filter, and the store only fetches those events for the aggregate. With conventional methods it's simply the event types your methods take.

For an `Evolve` method, the generator reads the method body. Every type it matches on counts: a declaration pattern (`case Foo foo:`), a bare type pattern (`case Foo:` or a `Foo =>` switch arm), a `case Foo when ...:` clause, and each type inside an `or` or parenthesized pattern. A `not Foo` pattern is skipped. Before 3.9.0 only declaration patterns counted, so events matched any other way were never fetched ([#1243](https://github.com/JasperFx/jasperfx/issues/1243)). A body that names no type at all leaves the filter empty, which means every event.

## Telling "the generator never ran" from "the generator declined your type"

`No source-generated dispatcher found` has two very different causes: the generator never ran against your assembly (a build configuration problem), or it ran and couldn't generate for your type (a code problem). The error message tells you which.

The generator writes `[assembly: JasperFxSourceGeneratorApplied]` into every compilation it's attached to, whether or not it found anything to generate, and the runtime reads it back from the aggregate's assembly and the projection's:

| The assembly carries | What the message tells you |
|---|---|
| `[JasperFxSourceGeneratorApplied]` | The generator ran and declined your type. It names the `partial` rules and `JFXEVT003`. |
| `[GeneratedEvolver]` but no marker | The same conclusion. An older generator, from before the marker, ran. |
| Neither | The generator most likely never ran here. It points at `ExcludeAssets="analyzers"` and `PrivateAssets="all"` on the package reference. |

The typical way to end up with no generator: a library references Marten with `PrivateAssets="all"`, and a test project references that library and declares the aggregates. That builds with no warnings and no evolvers ([marten#5495](https://github.com/JasperFx/marten/issues/5495)).

To catch that at build time instead, opt in per project:

```xml
<JasperFxEventsRequireSourceGenerator>true</JasperFxEventsRequireSourceGenerator>
```

The build then fails with `JFXEVT900` when no `JasperFx.Events.SourceGenerator` analyzer is attached to the project. It's opt-in because MSBuild can't tell a project that should have the generator from one with no aggregates at all, and because "the analyzer asset is suppressed" isn't proof of a mistake: some builds bundle the generator deliberately instead.

## The emitted-events manifest

The generator also records which events each handler method appends, read from the method body, as an assembly-level manifest that Wolverine reads:

```csharp
[assembly: JasperFx.Events.EmittedEvents(typeof(AcceptHomeCheckAssignmentHandler), "Handle",
    typeof(HomeCheckAssignmentAccepted),
    ParameterTypes = new System.Type[] { typeof(AcceptHomeCheckAssignment), typeof(IEventStream<HomeCheck>) })]
```

- A method is a candidate when a parameter is an `IEventStream<T>`, a parameter carries an `IRefersToAggregate` attribute (like Wolverine's `[WriteAggregate]`, `[WriteModel]` or `[DcbModel]`), or the return type, or a tuple element of it through `Task<T>` / `ValueTask<T>`, implements `ICarriesEvents`.
- Events are read from `AppendOne` / `AppendMany` on an `IEventStream<T>`, from the arguments of a constructor or factory returning an `ICarriesEvents` type, and from `Add` (or a collection initializer) on one. Only `object`-typed parameter slots count, so a stream id beside them is never mistaken for an event.
- Each overload gets its own entry, told apart by `ParameterTypes` ([#1021](https://github.com/JasperFx/jasperfx/issues/1021)). An overload whose parameter types can't be named by `typeof` at assembly scope is recorded without them.
- An event whose static type is `object` can't be inferred and is skipped. The manifest is a lower bound, and Wolverine's explicit `[Emits]` is the override.

## Where FastExpressionCompiler is still used

Not for projections. JasperFx still compiles a few expression trees with FastExpressionCompiler under the JIT. Member accessors (`LambdaBuilder`, which also sets an aggregate's `Version`), strong-typed identifier wrappers (`ValueTypeInfo`, `StrongTypedIdResolver`) and message publishing check `RuntimeFeature.IsDynamicCodeSupported` and take a non-compiling path in a Native AOT binary. The rest, such as discovering a DCB tag type's inner value with `TagTypeRegistration.Create<TTag>()`, are annotated `[RequiresDynamicCode]` so the AOT publish warns about them. See [Publishing AOT](/codegen/aot) for the list and the alternatives.
