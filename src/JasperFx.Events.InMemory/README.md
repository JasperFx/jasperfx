# JasperFx.Events.InMemory

An in-memory store for **prototyping** a Critter Stack application from stub types, before you've chosen
Marten, Polecat or Fisher. It's meant to be a short-lived step: swap it for a real store as soon as the
design settles.

Today the package carries the document half: `InMemoryDocumentStore`, an implementation of the
JasperFx.Events document contract (`IDocumentSessionFactory` / `IDocumentSessionOperations`) that runs
the same document compliance suites as the real stores.

```csharp
using JasperFx.Events.InMemory;

var store = new InMemoryDocumentStore();

await using var session = store.LightweightSession();
session.Store(new Incident { Id = Guid.NewGuid(), Title = "Printer on fire" });
await session.SaveChangesAsync();
```

What to know before you lean on it:

- **Commits are all-or-nothing.** If any part of a unit of work fails -- a refused optimistic
  concurrency check, say -- nothing from that unit lands.
- **Nothing is persisted.** Restart the process and the data is gone.
- **Queries are LINQ-to-objects** over snapshots of the stored documents. There's no query
  translation and no search.
