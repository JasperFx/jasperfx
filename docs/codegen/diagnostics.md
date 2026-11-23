# Build Diagnostics

The source generator in `JasperFx.Events` writes the dispatch code for your projections and aggregates at build time. When it finds something it can't generate, or something that would compile and then quietly do the wrong thing at runtime, it tells you at build time with a `JFXEVT` diagnostic instead of leaving you to find out in production.

You don't install anything for these. The generator and its analyzers ship inside the `JasperFx.Events` package, so anything that references Marten, Polecat or Fisher gets them.

## Projections and aggregates

| Id | Severity | What it means | What to do |
|---|---|---|---|
| `JFXEVT002` | Warning | The generator can't work out the identity type of an aggregate. | Add an `Id` property, or mark the identity member with `[AggregateIdentity]`. |
| `JFXEVT003` | Error | A projection whose dispatcher has to be generated into the class itself isn't `partial`. | Mark the class `partial`. See [Projection Apply Dispatch](/codegen/projection-apply-dispatch) for exactly which projections need it. |
| `JFXEVT005` | Info | An event projection's `ApplyAsync` writes a document through a call whose document type the generator can't name, so that type isn't registered as a published type. The store still creates its storage on demand, but schema creation, the known document types and rebuild teardown won't know about it ahead of time. | Call the session with a concrete document type. |
| `JFXEVT006` | Warning | An event projection writes documents from its `ApplyAsync` override but isn't `partial`, so the generator can't register them as published types. Nothing fails at runtime, but the ahead-of-time surfaces come up short as with `JFXEVT005`. | Declare the projection `partial`, or register the types with `RegisterPublishedType`. |
| `JFXEVT007` | Error | A `[BoundaryAggregate]` type has no `Evolve` method and no conventional `Apply` or `Create` methods, so there's nothing to fold events with, and `FetchForWritingByTags<T>()` would fail the first time it's called. | Add an `Evolve` method or the conventional methods. |
| `JFXEVT008` | Warning | An aggregate is an open generic type, or nested behind `private` or `protected`, so no generated evolver can name it. Nothing is generated for it. | For a nested type, make it and its containing types `internal` or `public`, or dispatch it from a `partial` projection declared beside it. For an open generic, the generator builds an evolver for each closed form it sees used, for example from `Snapshot<Order<Guid>>()` or a projection over a closed form. The warning stays quiet when something else in the compilation already covers the type. |
| `JFXEVT009` | Warning | A conventional handler (`Apply`, `Create`, `ShouldDelete`, `Project`, `Transform`, or a single-argument event constructor) isn't public. Generated dispatch only calls public members, so the projection compiles, runs, and never applies that event. | Make it `public`. If it's really a private helper that happens to share the name, rename it or mark it `[JasperFxIgnore]`. |

`JFXEVT008` arrived in 3.4.0 and `JFXEVT009` in 3.5.0. Both are the generator telling you about code that built and silently did nothing before, so an upgrade can surface them on code that "worked".

## Natural keys

These are all errors, added in 3.13.0. The runtime refuses the same setups when the projections are registered, so the diagnostics just move that failure up to build time.

| Id | What it means |
|---|---|
| `JFXEVT010` | An aggregate owns more than one natural key. It can own one, and map any number. |
| `JFXEVT011` | Two natural keys on one aggregate have the same type. Each key needs its own type. |
| `JFXEVT012` | A mapped natural key names its own aggregate as its source. |
| `JFXEVT013` | The source projection of a mapped natural key doesn't own a natural key. |
| `JFXEVT014` | A mapped natural key's source is itself a mapped key. Mapped keys can't be chained. |
| `JFXEVT015` | A mapped natural key doesn't have the type of its source's natural key. |
| `JFXEVT016` | A key type named on a Wolverine model attribute (`[WriteAggregate<TKey>]`, `[ReadAggregate<TKey>]`, `[WriteModel<TKey>]`, `[ReadModel<TKey>]` or `NaturalKey = typeof(TKey)`) isn't one of the aggregate's natural keys. |
| `JFXEVT017` | A key type is named as `TKey?`. Name it by its non-nullable type. |

## Making sure the generator ran at all

None of the diagnostics above can fire if the generator never ran, for example because a project reference excludes the `analyzers` asset. To fail the build in that case, opt in per project:

```xml
<JasperFxEventsRequireSourceGenerator>true</JasperFxEventsRequireSourceGenerator>
```

The build then fails with `JFXEVT900` when no `JasperFx.Events.SourceGenerator` analyzer is attached to the project. See [Projection Apply Dispatch](/codegen/projection-apply-dispatch#telling-the-generator-never-ran-from-the-generator-declined-your-type) for why it's opt-in.
