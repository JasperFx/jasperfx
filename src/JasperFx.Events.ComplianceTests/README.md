# JasperFx.Events.ComplianceTests

Shared behavioral compliance test suites for Critter Stack event stores — the same event sourcing
expectations asserted against [Marten](https://martendb.io) (PostgreSQL), Polecat (SQL Server), and
any future store built on `JasperFx.Events`.

This package ships **C# source, not a compiled assembly**. That is forced, not a preference:
`JasperFx.Events.SourceGenerator` emits aggregate dispatchers per consuming assembly and binds each
product's own session type, so the shared aggregates have to be compiled inside each consumer.
Compiling in the consumer also sidesteps package-version skew and `LangVersion`/`ImplicitUsings`
differences between the repos.

## Using it

Reference the package from a test project that already has xunit v3 and Shouldly, then supply three
things.

**1. Five global aliases naming your store's own types.** The shared suites declare aggregates and
projections at file scope, so they cannot reach the `<TOperations, TQuerySession>` pair the suite
classes are generic over. The source generator resolves these by type name, so aliases are enough:

```csharp
global using ComplianceQuerySession = Marten.IQuerySession;
global using ComplianceOperations = Marten.IDocumentOperations;
global using ComplianceEventProjection = Marten.Events.Projections.EventProjection;
global using ComplianceStringPartyProjectionBase =
    Marten.Events.Aggregation.SingleStreamProjection<
        JasperFx.Events.ComplianceTests.StringQuestParty, string>;
global using ComplianceMultiStreamProjectionBase =
    Marten.Events.Projections.MultiStreamProjection<
        JasperFx.Events.ComplianceTests.ComplianceDepartment, string>;
```

`ComplianceQuerySession` binds the `EvolveAsync(IEvent, …)` convention on the self-aggregating
fixtures; the next two bind the EventProjection suites to your product's own projection base and
writable session. The last two are *closed* generics, because the single stream and multi stream
projection bases are generic over both the document and its identity — they bind the string-identity
and multi-stream suites' custom projections to your product's `SingleStreamProjection<TDoc, TId>`
and `MultiStreamProjection<TDoc, TId>`.

**2. A concrete fixture** closing `EventStoreComplianceFixture<TOperations, TQuerySession>` over your
store's session pair. Everything portable in the suites runs through the shared JasperFx surfaces
(`IEventStoreOperations`, `IEventRegistry`, `IProjectionDaemon`); the fixture only has to supply what
no shared interface declares — store construction from a `ComplianceStoreConfig`, session
acquisition, `SaveChangesAsync`, document load-back, batched DCB queries, and teardown.

**3. Two partial classes**, for the two suites whose shared type cannot be reached by an alias.

`FlatTableProjectionCompliance` is the first: every product's flat-table projection base takes
constructor arguments describing where the table lives, and those signatures genuinely differ, so no
single `base(...)` call satisfies all of them. Declaring the primary key column is per-product for
the same reason — that API hangs off each dialect's own `Table` type. The library owns the table
name, the projection name and every event mapping; a consumer supplies the rest:

```csharp
namespace JasperFx.Events.ComplianceTests;

public partial class ComplianceFlatTableProjection : FlatTableProjection
{
    public ComplianceFlatTableProjection() : base(TableName, SchemaNameSource.DocumentSchema)
    {
        Table.AddColumn<Guid>("id").AsPrimaryKey();   // your dialect's column API
        ConfigureMappings();                          // everything portable
    }
}
```

If your base takes a literal schema name rather than resolving the store's, pass
`ComplianceFlatTableProjection.SchemaName` — the suite configures its store with the same constant,
and the two have to agree or the projection writes into a table the suite is not reading.

`SubscriptionCompliance` is the second, for a subtler reason. Both products declare `ISubscription`
with an *identical* member — `Task<IChangeListener> ProcessEventsAsync(EventRange,
ISubscriptionController, IDocumentOperations, CancellationToken)` — but `IChangeListener` is a
per-product type, so the signature cannot be written once. The library owns the recording, the
waiting and the subscription name; a consumer supplies only the interface implementation:

```csharp
namespace JasperFx.Events.ComplianceTests;

public partial class ComplianceSubscription : ISubscription   // your product's ISubscription
{
    public Task<IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, CancellationToken cancellationToken)
    {
        Record(page.Events);
        return Task.FromResult<IChangeListener>(NullChangeListener.Instance);
    }
}
```

Your registrar's `Subscribe` should pin the name to `ComplianceSubscription.SubscriptionName`;
progression is keyed on it and the products disagree on what an unnamed subscription defaults to.

Then enroll each suite with an empty subclass:

```csharp
public class dcb_tag_query_and_consistency_compliance
    : DcbTagQueryAndConsistencyCompliance<MyComplianceFixture, IDocumentOperations, IQuerySession>;
```

### Opt-in capability suites

Several suites cover capabilities that are opt-in rather than part of the baseline event contract,
and their seam members on `IComplianceStoreRegistrar` carry throwing defaults: a store that has not
implemented the capability does not enroll, never reaches the member, and keeps compiling.

`AggregateWriteCacheCompliance` (jasperfx#674) is the oldest of them. `IAggregateWriteCache` is a
*baseline-only* cache — the stream version and every event after the cached version are still read on
every fetch — so the suite's job is to prove that turning it on is unobservable except in latency,
including when the cached baseline is wrong. Note the shape of the load-bearing assertion: every
correctness fact about caching is vacuously true of a store that ignored the opt-in entirely, so the
suite supplies its own `RecordingAggregateWriteCache` and asserts a nonzero hit count. Same reasoning
as the gzipped serializer in `BinaryEventSerializationCompliance`.

`CompositeProjectionCompliance` (jasperfx#725) is opt-in through
`IComplianceStoreRegistrar.AddCompositeProjection`. It needs a seam because a composite cannot be
constructed by a suite at all: every product subclasses the shared
`CompositeProjection<TOperations,TQuerySession>` and keeps the constructor internal, since it needs the
store's options, so a composite only comes into being through that product's own
`Projections.CompositeProjectionFor(name, configure)` — whose `configure` parameter is typed to the
product's own subclass. The implementation is a forward plus a small adapter, because the *calls* are
identical across the products even where the types are not; all three expose
`Snapshot<T>(int stageNumber)` on their composite, and `IComplianceCompositeBuilder` declares its own
void-returning member only because Marten's returns a mapping expression while Polecat's and Fisher's
return void:

```csharp
public void AddCompositeProjection(string name, Action<IComplianceCompositeBuilder> configure)
    => _options.Projections.CompositeProjectionFor(name, c => configure(new Builder(c)));
```

Its load-bearing fact is the rebuild one, and the members are additive for that reason: a store that
replayed the stream over surviving rows instead of tearing the member down reads back exactly doubled,
which a last-write-wins aggregate would hide. Two of the three products already carry a *local* test
named for that class of bug — `Bug_439_composite_member_teardown` and `composite_member_teardown` —
which is what marks the behaviour as shared rather than product-owned.

`SingleTenantedEventSlicingCompliance` (jasperfx#724) is opt-in for a different reason: it needs no
registrar seam at all, but the precondition it constructs is unusual enough that a store should adopt
it knowingly. On a single-tenanted store, events whose `tenant_id` values disagree must still fold into
one aggregate — wolverine#2053 / marten#4085 — and only the *async daemon* ever got that wrong, so the
suite drives the daemon rather than asserting on a live aggregate. Note its guard: it stamps
disagreeing tenant ids through `IEvent.TenantId`, then reads the events back and **skips** if the store
normalised them away, because an unmixed store satisfies the assertion for a reason that has nothing to
do with the behaviour under test. Vacuous green is worse than a skip, since only one of the two is
visible.

## Capability gates

Where a store genuinely cannot support a behavior, override the `virtual bool Supports...` flags on
the fixture; the affected tests skip rather than fail. Gates are meant to be temporary and tracked —
a suite failing on your store is usually a product bug, not a test to soften.

## Local dev loop

Both current consumers accept a `ComplianceSourceDir` property that swaps the published suites for a
working copy, so a new wave can be validated against a real store before the JasperFx release:

```bash
dotnet test src/EventSourcingTests/EventSourcingTests.csproj -f net9.0 \
    -p:ComplianceSourceDir=/path/to/jasperfx/src/JasperFx.Events.ComplianceTests
```

## What is in scope

The library asserts behavior that every `JasperFx.Events` store owes its users, reached through the
shared interfaces (`IEventStoreOperations`, `IQueryEventStore`, `IEventRegistry`, `IEventStore`,
`IProjectionDaemon`). Covered today:

| Area | Suite |
|---|---|
| Self-aggregating `EvolveAsync` conventions | `SelfAggregatingEvolveCompliance` |
| DCB tag queries and consistency | `DcbTagQueryAndConsistencyCompliance` |
| `AssignTagWhere` | `AssignTagWhereCompliance` |
| Async daemon smoke + rebuild | `AsyncDaemonCompliance` |
| Aggregate type auto-discovery | `AutoDiscoveredAggregateCompliance` |
| `EventProjection` registration and enrichment | `EventProjectionRegistrationCompliance`, `EventProjectionEnrichmentCompliance` |
| Rebuild concurrency cap resolution | `RebuildConcurrencyCapCompliance` |
| Session correlation / causation from `Activity` | `ActivityCorrelationCompliance` |
| String stream identity, single stream projections | `StringIdentitySingleStreamCompliance` |
| Write handles and stream concurrency | `FetchForWritingCompliance` |
| The second-level `FetchForWriting` snapshot cache | `AggregateWriteCacheCompliance` |
| Stream reads, time travel, stream state | `StreamReadCompliance` |
| The `IEvent` envelope contract | `EventMetadataCompliance` |
| Live aggregation, including last-known | `LiveAggregationCompliance` |
| `FetchLatest` / `ProjectLatest` across lifecycles | `FetchLatestCompliance` |
| Archiving a stream and its consequences | `StreamArchivingCompliance` |
| The event store explorer surface | `EventStoreExplorerCompliance` |
| Flat-table event projections | `FlatTableProjectionCompliance` |
| String stream identity, read and write surface | `StringStreamIdentityCompliance` |
| Multi-stream projection grouping and fan-out | `MultiStreamProjectionCompliance` |
| Snapshot lifecycle equivalence (Inline / Async / Live) | `SnapshotLifecycleCompliance` |
| Strong-typed identifiers on aggregates | `StrongTypedIdentityCompliance` |
| Stream compacting into a `Compacted<T>` snapshot | `StreamCompactingCompliance` |
| Batch data masking of stored events | `EventDataMaskingCompliance` |
| Projection rebuild and catch-up semantics | `RebuildAndCatchUpCompliance` |
| The projection error path and dead letters | `DeadLetterCompliance` |
| Conjoined (per-tenant) event tenancy | `ConjoinedEventTenancyCompliance` |
| The cross-stream event query (`QueryEventsAsync`) | `EventQueryCompliance` |
| Subscriptions | `SubscriptionCompliance` |
| Single-tenanted slicing of disagreeing tenant ids | `SingleTenantedEventSlicingCompliance` |
| Composite projections — staging and member teardown | `CompositeProjectionCompliance` |

### Identity-less boundary aggregates (jasperfx#718)

`DcbTagQueryAndConsistencyCompliance` carries one aggregate — `CourseLoad` — with no `Id` and no
`[AggregateIdentity]`, marked `[BoundaryAggregate]`. It is there because every other DCB aggregate in
the suite happens to have an identity, so a store could require one for a *boundary* aggregate and
still pass the whole suite. Both SQL stores did: Fisher threw from its own identity resolution
(fisher#135), Polecat from `DocumentMapping`'s constructor (polecat#521), despite the marker being
the documented cross-stack answer.

Its two facts fold **with matching events present**, and that is load-bearing rather than incidental.
`FetchForWritingByTags` only resolves the aggregator when the query finds events, so a boundary over
an empty result — the ordinary "this must not exist yet" assertion — succeeds on a store that cannot
fold the type at all.

The type is declared in the shared source rather than as a per-consumer partial, which works because
the package ships as source: the attribute lands on the aggregate in the consumer's own compilation,
which is both the compilation the generator emits `[GeneratedEvolver]` into and the assembly the
runtime scans as `typeof(TDoc).Assembly`. It is deliberately never registered — no snapshot, no live
aggregation — since a DCB aggregate is discovered lazily on first use, and registering it eagerly
would move the failure to store construction, where it would take every other fact in the suite down
with it.

### The document contract (jasperfx#647)

A second, independent family covers the small *document* slice that `JasperFx.Events` now abstracts
alongside the event store — `JasperFx.Events.Documents`:

| Area | Suite |
|---|---|
| Session opening and the transaction boundary | `DocumentSessionCompliance` |
| `Store` and `LoadAsync` — `Guid`, `string` and strong-typed identities | `DocumentLoadAndStoreCompliance` |
| `Delete`, its identity overloads, and `DeleteWhere` | `DocumentDeleteCompliance` |
| `Query<T>()`, its minimum translatable operator set, and the async terminators | `DocumentQueryCompliance` |
| The route from a session to its event store | `DocumentSessionEventsCompliance` |
| The stream actions a session has queued but not committed | `PendingStreamActionsCompliance` |
| Post-commit session listeners and the change set they receive | `DocumentCommitListenerCompliance` |

Enrollment is deliberately much cheaper than the event side. `DocumentStorageComplianceFixture` has
**three** abstract members — build a store, hand back an `IDocumentSessionFactory`, wipe the data —
and is **not generic** over the store's session pair, because everything the document suites do runs
through the shared contracts. That asymmetry is the result being demonstrated, not an inconsistency:
if a document suite ever needs a fixture member that reaches past the interfaces, the contract has a
hole and the contract is what should change.

```csharp
public class my_document_fixture : DocumentStorageComplianceFixture
{
    protected override Task BuildStoreAsync(DocumentComplianceConfig config) { /* ... */ }
    public override IDocumentSessionFactory Sessions => _store;
    public override Task CleanDocumentDataAsync() { /* ... */ }
}

public class document_query_compliance : DocumentQueryCompliance<my_document_fixture>;
```

`DocumentSessionEventsCompliance` and `PendingStreamActionsCompliance` are opt-in: alone among the
document suites they need the store to be an *event* store as well, so a document-only implementer
simply does not enroll them. Both members they cover —
`IDocumentReadOperations.Events` / `IDocumentSessionOperations.Events` (jasperfx#669) and
`IDocumentSessionOperations.PendingStreams` (jasperfx#673) — ship with throwing defaults, and both
are reachable by a near-miss that the compiler does not catch: C# interface implementation is not
return-type covariant, so a session already declaring a member of the same name with the product's
own type binds to the default instead of implementing the contract. Only a test calling through a
contract-typed session notices.

`DocumentCommitListenerCompliance` (jasperfx#679) is opt-in for a different reason: it needs only
documents, so any store implementing the document contract can enroll, but it needs `BuildStoreAsync`
to replay `config.CommitListeners` onto the store's own listener collection — `StoreOptions.Listeners`
on all three products. That member exists because registration happens when the store is *built*,
before any session exists, so a suite working only through the sessions the fixture hands out could
not register a listener at all.

It is also the suite whose failure mode nothing else can reach. Neither `IDocumentCommitListener` nor
`IDocumentChangeSet` ships a default implementation, so unlike the two members above, a store that
declares them wrongly gets a compile error rather than a silent bind to a throwing default. What no
compiler sees is the *wiring*: a store that declares both interfaces perfectly and never invokes the
listener builds clean and passes every other suite here. Deleting the listener loop from this repo's
own in-memory reference store fails 8 of the suite's 10 facts; the 2 that still pass are the two that
assert the listener does **not** fire.

Two behaviors it deliberately does not assert, because the products disagree and the contract permits
both: an **empty unit of work** (Fisher short-circuits and raises nothing; Marten matches but never
stated it) and a session **enlisted in a caller's ambient transaction** (Fisher does not fire, since
the enclosing transaction rather than `SaveChangesAsync` is what makes the data durable; Marten fires
unconditionally). The second is unreachable from a suite in any case — enlistment is spelled on each
product's own `SessionOptions`, which `IDocumentSessionFactory` does not expose — so it belongs in
each store's own tests.

`BuildStoreAsync` must honor `config.ValueTypes` as well as `config.DocumentTypes` — every store
spells that `options.RegisterValueType(type)`. It is what lets `DocumentLoadAndStoreCompliance` hold
the `LoadAsync<T>(object)` overload (jasperfx#665) to a definition; a fixture that ignores it fails
the strong-typed identity tests rather than skipping them.

The same goes for `config.StreamIdentity`, which is nullable — leave the store on its own default
when it is null, and set it when it is not. Only the event-capable document suites populate it, and
they do so because they append by stream *key*. This is the one knob whose absence was a suite bug
rather than a fixture's (jasperfx#672): `DocumentSessionEventsCompliance` needed string stream
identity and had no way to say so, so three of its five facts failed on every store defaulting to
Guid, with an error naming stream identity but nothing about the suite's requirement. A precondition
a config cannot carry is a precondition each fixture has to guess.

That overload also shows what these suites are *for*. It ships with a default implementation, so a
store takes the JasperFx bump without a compile break — the default forwards a boxed `Guid` or
`string` and throws on anything else. Nothing in the compiler then tells the store it has only half
the member. `DocumentLoadAndStoreCompliance` does: a store that inherits the default fails the
strong-typed facts. Where the contract's defaults deliberately stop breaking builds, the suite is
what is left holding stores to the behavior.

These suites are the one part of the library that is executed inside this repo as well as by its
consumers: `EventStoreTests` enrolls an in-memory reference implementation, so the shared definition
is known to be satisfiable before three products are held to it. That reference implementation is a
test double, not a product — it exists to keep the suite honest.

Note that a consumer whose test project carries a global `using Marten;` (or the Polecat equivalent)
will hit an ambiguity between that product's async LINQ terminators and
`JasperFx.Events.Documents.DocumentQueryableExtensions`, which share names and receiver types.
Scoping or removing the global using for the compliance compile resolves it.

## What is deliberately out of scope

Storage layout and DDL, table partitioning, node distribution / HotCold, and high-water detection
internals. If a behavior only makes sense in terms of one engine's storage, it belongs in that
product's own test suite, not here.

**General LINQ and query-provider behavior is out of scope permanently**, not pending a contract. The
stores' query languages diverge structurally enough that a shared suite would pin coincidence rather
than contract. This has been the position since the library was designed; it is restated here
because it had drifted into "blocked until a shared document store contract exists", which wrongly
reads as deferred work. It is not deferred. A file like `Polecat.Tests/Linq/additional_linq_operator_tests.cs`
is a product-owned test file, not a port awaiting absorption (marten#5155).

`DocumentQueryCompliance` is not a counter-example to that, and must not be allowed to grow into
one. It pins a closed **minimum translatable set** — `Where`, `Select`, `OrderBy` /
`OrderByDescending`, `ThenBy` / `ThenByDescending`, `Take`, `Skip`, `Distinct` — because a consumer
holding only an `IQueryable<T>` has no way to discover whether a store translates those or silently
does not. That set is closed by measurement (the operators `CritterWatch.Services` actually applies
to a `Query<T>()` chain), not open by principle. Operators outside it stay product-owned however many
stores happen to support them.

Session semantics *are* now in scope, via the document contract above. The rest of the document-db
side — patching, bulk insert, LINQ joins / grouping / `Include`, soft-delete semantics, document
metadata, session listeners, the stores' `Advanced` surfaces and schema management — stays out, and
that exclusion is now a settled boundary rather than an open question: those are the surfaces
jasperfx#647 deliberately declined to abstract.

New cross-store event sourcing behavior should land as a compliance suite first, and only then be
enrolled by each product.
