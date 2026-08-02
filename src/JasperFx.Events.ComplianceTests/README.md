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

**1. A global alias naming your store's read session.** The shared self-aggregating fixtures declare
`EvolveAsync(IEvent, ComplianceQuerySession)`; the source generator resolves the parameter by type
name, so an alias is enough:

```csharp
global using ComplianceQuerySession = Marten.IQuerySession;
```

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
