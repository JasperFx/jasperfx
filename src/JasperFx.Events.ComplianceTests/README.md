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

Reference the package from a test project that already has xunit v3 and Shouldly, then supply two
things.

**1. Four global aliases naming your store's own types.** The shared suites declare aggregates and
projections at file scope, so they cannot reach the `<TOperations, TQuerySession>` pair the suite
classes are generic over. The source generator resolves these by type name, so aliases are enough:

```csharp
global using ComplianceQuerySession = Marten.IQuerySession;
global using ComplianceOperations = Marten.IDocumentOperations;
global using ComplianceEventProjection = Marten.Events.Projections.EventProjection;
global using ComplianceStringPartyProjectionBase =
    Marten.Events.Aggregation.SingleStreamProjection<
        JasperFx.Events.ComplianceTests.StringQuestParty, string>;
```

`ComplianceQuerySession` binds the `EvolveAsync(IEvent, …)` convention on the self-aggregating
fixtures; the next two bind the EventProjection suites to your product's own projection base and
writable session. The last one is a *closed* generic, because the single stream projection base is
generic over both the document and its identity — it binds the string-identity suite's custom
projection to your product's `SingleStreamProjection<TDoc, TId>`.

**2. A concrete fixture** closing `EventStoreComplianceFixture<TOperations, TQuerySession>` over your
store's session pair. Everything portable in the suites runs through the shared JasperFx surfaces
(`IEventStoreOperations`, `IEventRegistry`, `IProjectionDaemon`); the fixture only has to supply what
no shared interface declares — store construction from a `ComplianceStoreConfig`, session
acquisition, `SaveChangesAsync`, document load-back, batched DCB queries, and teardown.

Then enroll each suite with an empty subclass:

```csharp
public class dcb_tag_query_and_consistency_compliance
    : DcbTagQueryAndConsistencyCompliance<MyComplianceFixture, IDocumentOperations, IQuerySession>;
```

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
| String stream identity | `StringIdentitySingleStreamCompliance` |
| Write handles and stream concurrency | `FetchForWritingCompliance` |
| Stream reads, time travel, stream state | `StreamReadCompliance` |
| The `IEvent` envelope contract | `EventMetadataCompliance` |
| Live aggregation, including last-known | `LiveAggregationCompliance` |

## What is deliberately out of scope

Storage layout and DDL, table partitioning, node distribution / HotCold, high-water detection
internals, and anything on the document-db side (LINQ, patching, session semantics) — the last
because no shared document store contract exists yet. If a behavior only makes sense in terms of one
engine's storage, it belongs in that product's own test suite, not here.

New cross-store event sourcing behavior should land as a compliance suite first, and only then be
enrolled by each product.
