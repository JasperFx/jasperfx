# JasperFx.Events.InMemory

An in-memory store for **prototyping** a Critter Stack application from stub types, before you've chosen
Marten, Polecat or Fisher. It's meant to be a short-lived step: swap it for a real store as soon as the
design settles.

The package carries `InMemoryDocumentStore`, an implementation of the JasperFx.Events document contract
(`IDocumentSessionFactory` / `IDocumentSessionOperations`) that runs the same document compliance suites
as the real stores, plus an event store behind `session.Events`: start streams, append (including
optimistic appends with expected versions), and read streams and stream state back. Events commit in the
same unit of work as documents. Live aggregation (`FetchForWriting`, `FetchLatest`,
`AggregateStreamAsync`) and inline projections are still being added.

Register it with the deliberately awkward name:

```csharp
using JasperFx.Events.InMemory;

builder.Services.AddInMemoryStoreForPrototyping(x =>
{
    x.ConfigureDocuments(store => store.OptimisticConcurrencyTypes.Add(typeof(Incident)));
});
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
- **Out of scope:** the async projection daemon, archiving and compacting streams, rewriting events, and
  tag (DCB) queries. Those throw a `NotSupportedException` that says so.
- **Queries are LINQ-to-objects** over snapshots of the stored documents. There's no query
  translation and no search.
