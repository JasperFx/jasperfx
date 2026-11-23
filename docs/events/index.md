# Event Store Abstractions

Marten, Polecat and Fisher are three different event stores on three different databases, but they don't each have their own idea of what an event, a stream or a projection is. Those ideas live here, in the `JasperFx.Events` package, and every store builds on them. You usually take `JasperFx.Events` through one of the stores rather than referencing it yourself.

That shared core pays off twice. The stores behave the same way, because they run the same projection and daemon code and pass the same compliance suites. And code written against the abstractions compiles once against any of them, which is how Wolverine's aggregate handler workflow and CritterWatch work across every store.

## What's in it

- **Events and streams.** `IEvent` and `IEvent<T>` carry an event with its metadata (sequence, version, stream, tenant, timestamp, tags). `StreamState` describes a stream, and `IEventStream<T>` is a stream you've fetched to append to, holding the aggregate's current state.
- **Store-agnostic operations.** `IEventOperations` and `IEventStoreOperations` cover the write side: `StartStream`, `Append`, `AppendOptimistic`, `FetchForWriting`, `FetchForExclusiveWriting` and `WriteToAggregate`. `IQueryEventStore` covers the read side: `FetchStreamAsync`, `FetchStreamStateAsync`, `AggregateStreamAsync` and `LoadAsync`. The small document slice a store-agnostic caller needs alongside the events is in `JasperFx.Events.Documents`.
- **Projections.** The single-stream, multi-stream, event and composite projection bases the stores' own projection types derive from, along with grouping, slicing, enrichment and side effects. Your conventional `Apply`, `Create` and `ShouldDelete` methods are dispatched by a source generator at build time rather than by reflection. See [Projection Apply Dispatch](/codegen/projection-apply-dispatch) and [Build Diagnostics](/codegen/diagnostics).
- **The async daemon.** The machinery that runs async projections and subscriptions: shards, high water detection, error handling, rebuilds, blue/green projection versions, and [parking a failing identity](/events/parked-identities). Each store supplies the storage, and the daemon is the same everywhere.
- **Dynamic consistency boundaries.** Tag types, `EventTagQuery` and `EventBoundary<T>`, for a rule that spans several streams without a distributed transaction.
- **Natural keys, archiving, masking and compaction**, all defined once and implemented per store.
- **The `projections` command.** Running, rebuilding, listing and repairing projections from the command line works the same against every store.

## Writing code against the abstractions

A command handler that takes an `IEventStream<T>` doesn't care which store fetched it:

```csharp
public static void Handle(Withdraw command, [WriteAggregate] IEventStream<Account> stream)
{
    if (stream.Aggregate is null || stream.Aggregate.Balance < command.Amount)
        throw new InvalidOperationException("Insufficient funds");

    stream.AppendOne(new Withdrawn(command.Amount));
}
```

That also makes it easy to test without a database. See [Unit Testing Handlers](/events/unit-testing-handlers).

How far portability goes is a matter of API, not behavior: the same call compiles against every store, and the compliance suites hold the stores to the same results, but each store still has its own database, its own performance profile and its own extras beyond the shared surface.

## Prototyping before you choose a store

The `JasperFx.Events.InMemory` package is an in-memory document and event store that runs the real projection and daemon code, so you can build an application from stub types before you commit to a database:

```csharp
builder.Services.AddInMemoryStoreForPrototyping();
```

It's deliberately short-lived, and it refuses to start in Production unless you explicitly allow it.

## For event store authors

A store implements `IEventStore` and `IEventDatabase`, derives its operations from `EventOperationsBase`, and enrolls in the behavioral suites in `JasperFx.Events.ComplianceTests`, which ships as source. New members on these interfaces arrive with default implementations, so a store built against an earlier 3.x release keeps compiling and loading. The [migration guide](/migration-guide) lists what each release adds for store authors.

## Where to go next

The user-facing documentation for event sourcing lives with each store:

- [Marten](https://martendb.io) on PostgreSQL
- [Polecat](https://polecat.jasperfx.net) on SQL Server
- [Fisher](https://fisher.jasperfx.net)
