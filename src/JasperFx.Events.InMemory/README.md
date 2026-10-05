# JasperFx.Events.InMemory

An in-memory store for **prototyping** a Critter Stack application from stub types, before you've chosen
Marten, Polecat or Fisher. It's meant to be a short-lived step: swap it for a real store as soon as the
design settles.

The package carries `InMemoryDocumentStore`, an implementation of the JasperFx.Events document contract
(`IDocumentSessionFactory` / `IDocumentSessionOperations`) that runs the same document compliance suites
as the real stores, plus an event store behind `session.Events`: start streams, append (including
optimistic appends with expected versions), and read streams and stream state back. Events commit in the
same unit of work as documents, and aggregates fold live from their streams with `AggregateStreamAsync`,
`FetchForWriting`, `FetchLatest` and `WriteToAggregate`.

Inline projections run inside that same commit: register an inline snapshot with
`store.Projections.Snapshot<T>(SnapshotLifecycle.Inline)`, or a projection class with
`store.Projections.Add(projection, ProjectionLifecycle.Inline)`. The projection base classes are in
`JasperFx.Events.InMemory.Projections` and named like Marten's and Fisher's -- `SingleStreamProjection<TDoc, TId>`,
`MultiStreamProjection<TDoc, TId>` and `EventProjection` -- so a projection moves to a real store with a
`using` change. If a projection throws, the events and documents from that unit of work are rolled back
with it.

Live aggregation folds with the source-generated `Apply` / `Create` dispatchers -- there's no runtime
fallback -- and this package carries `JasperFx.Events.SourceGenerator` as an analyzer, so any project that
references it runs the generator over its aggregates. An aggregate doesn't need an `Id` yet: a stub with no
identity member takes its stream's id type. The generator learns that id type from a call site such as
`FetchLatest<T>("key")` or `AggregateStreamAsync<T>(id)`, so a stub that's only ever *started* with a
string key needs one of those calls somewhere before it can be folded.

Register it with the deliberately awkward name:

```csharp
using JasperFx.Events.InMemory;

builder.Services.AddInMemoryStoreForPrototyping(x =>
{
    x.ConfigureDocuments(store => store.OptimisticConcurrencyTypes.Add(typeof(Incident)));

    x.ConfigureEvents(events =>
    {
        events.StreamIdentity = StreamIdentity.AsString;
        events.CorrelationIdEnabled = true;
    });

    x.ConfigureProjections(projections =>
    {
        projections.Snapshot<Incident>(SnapshotLifecycle.Inline);
        projections.Add(new IncidentHistoryProjection(), ProjectionLifecycle.Inline);
    });
});
```

The configuration is applied documents first, then events, then projections, whatever order you call
them in, because a projection is closed over the stream identity when it's registered.

The registration exposes the store as `IDocumentSessionFactory`, so code written against the
store-agnostic contract runs against it unchanged:

```csharp
var sessions = services.GetRequiredService<IDocumentSessionFactory>();

await using var session = sessions.LightweightSession();
var stream = await session.Events.FetchForWriting<Incident>(incidentId);
stream.AppendOne(new IncidentAssigned("agent-7"));
await session.SaveChangesAsync();
```

Every host start logs a warning that the in-memory store is in use, and a host whose environment is
`Production` refuses to start. A test host that never set `DOTNET_ENVIRONMENT` lands in `Production`
by default -- set it to `Development`, or opt in with `x.AllowStartingInTheProductionEnvironment = true`.

You can also use the store directly:

```csharp
var store = new InMemoryDocumentStore();

await using var session = store.LightweightSession();
session.Store(new Incident { Id = Guid.NewGuid(), Title = "Printer on fire" });
await session.SaveChangesAsync();
```

What to know before you lean on it:

- **Commits are all-or-nothing.** If any part of a unit of work fails -- a refused optimistic
  concurrency check, say -- nothing from that unit lands.
- **Nothing is persisted.** Restart the process and the data is gone.
- **Out of scope:** the async projection daemon (so Async projections are refused), archiving and compacting streams, rewriting events, and
  tag (DCB) queries. Those throw a `NotSupportedException` that says so.
- **Queries are LINQ-to-objects** over snapshots of the stored documents. There's no query
  translation and no search.
