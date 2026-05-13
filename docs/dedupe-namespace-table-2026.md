# Critter Stack 2026 — Dedupe Namespace Movement Table

Canonical reference for the dedupe pillar ([JasperFx#214](https://github.com/JasperFx/jasperfx/issues/214)) of the Critter Stack 2026 wave. Each row records a type whose home is moving as part of the Marten ↔ Polecat consolidation.

**Marten 9, Polecat 4, and Wolverine 6 migration guides must reference this table** for the relocations relevant to their product. The intent is one canonical list rather than per-product re-statements of the same moves.

Status legend:

* ✅ **Landed in JFx.Events 2.0** — type exists at the new home, downstream products still own their own copy.
* 🔄 **In flight** — design settled, awaiting product-side consumption PRs.
* 🕓 **Pending** — design discussion still open (see footnotes / open questions).

## JasperFx.Events 2.0 — new canonical home

| Type | Old namespace(s) | New namespace | Status | Affects |
|---|---|---|---|---|
| `IEventStream<T>` | `Marten.Events` / `Polecat.Events` | `JasperFx.Events` | ✅ | Marten 9, Polecat 4, Wolverine 6 |
| `ConcurrencyStyle` | `Wolverine.Marten` / `Wolverine.Polecat` | `JasperFx.Events` | ✅ | Wolverine 6 (and any consumer of the aggregate-handler workflow) |
| `StreamState` | `Marten.Events` / `Polecat.Events` | `JasperFx.Events` (was already partially there; constructors aligned in 2.0) | ✅ | Marten 9, Polecat 4 |
| `IEventFilter` | n/a (new contract) | `JasperFx.Events.Daemon` | ✅ | Marten 9, Polecat 4 — products satisfy by translating to their own filter representation |
| `IProjectionDatabase` | n/a (new contract) | `JasperFx.Events.Daemon` | ✅ | Marten 9, Polecat 4 — minimal seam for coordinator |
| `IDaemonChangeListener` (was Polecat's `IChangeListener`) | `Polecat` | `JasperFx.Events.Daemon` | ✅ | Polecat 4 (renamed from `IChangeListener` to avoid clash with Marten's session-level listener concept) |
| `IQueryEventStore` (session read-side) | `Marten.Events` / `Polecat.Events` | `JasperFx.Events` | ✅ | Marten 9, Polecat 4 — LINQ-returning methods stay on each product's extending interface |
| `IEventOperations` (session write-side basics) | `Marten.Events` / `Polecat.Events` (Polecat collapses w/ IEventStoreOperations) | `JasperFx.Events` | ✅ | Marten 9, Polecat 4. Polecat refactors its combined interface into the Marten 3-tier shape (`IQueryEventStore` + `IEventOperations` + `IEventStoreOperations`). |
| `IEventStoreOperations` (aggregate-handler workflow) | `Marten.Events` / `Polecat.Events` | `JasperFx.Events` | ✅ | Marten 9, Polecat 4, Wolverine 6. Excluded from this round: `FetchForWritingByTags<T>` (DCB — blocked on Polecat DCB parity) and `StreamLatestJson<T>` (Marten-specific optimization). |
| `IProjectionCoordinator`, `IProjectionCoordinator<T>` | `Marten.Events.Daemon.Coordination` | `JasperFx.Events.Daemon` | ✅ | Marten 9, Polecat 4. Non-generic interface lifted (depends on `Microsoft.Extensions.Hosting`, already a transitive dep of JFx core). Generic variant relaxed from `where T : IDocumentStore` to `where T : class` — products keep their own typed registrations. |
| `IProjectionDistributor`, `IProjectionSet`, `ProjectionLockIds` | `Marten.Events.Daemon.Coordination` | `JasperFx.Events.Daemon` | ✅ | Marten 9, Polecat 4 — Polecat to add a distributor layer ([Polecat follow-up issue](#polecat-follow-up-issues)). Abstract base class for the three Solo/SingleTenant/MultiTenanted variants deferred to the consumption PR so it's designed against real call sites. |
| Fetch planner family (`IFetchPlanner`, `IAggregateFetchPlan<TDoc,TId>`, `Async/Inline/Live/NaturalKeyFetchPlanner`) | `Marten.Events.Fetching` | `JasperFx.Events.Fetching` (new namespace) | 🔄 | Marten 9 source side; Polecat 4 adoption |
| `NaturalKey*` runtime types (`NaturalKeyDefinition`, `NaturalKeyBuilder`) | already in JFx.Events | n/a | ✅ | Marten 9 / Polecat 4 should drop their copies |
| `NaturalKeyProjection`, `NaturalKeyAttribute` | `Marten.Events.Aggregation` / `Polecat.Aggregation` | `JasperFx.Events.Aggregation` | 🔄 | Marten 9 source side; Polecat 4 adoption |
| `StreamCompactingRequest<T>` + `CompactStreamAsync<T>` overloads | `Marten.Events` / `Polecat.Events.Protected` | `JasperFx.Events` | 🕓 | Data class plus the two `IEventOperations.CompactStreamAsync<T>` overloads. Execution is product-specific; lifting the data class is a follow-up PR. |
| `IEventBoundary<T>`, `EventTagQuery` DCB write-side methods | `Marten.Events.Dcb` / `Polecat.Events.Dcb` | `JasperFx.Events.Tags` | 🕓 | Blocked on Polecat 4 DCB parity — see [Polecat follow-up issues](#polecat-follow-up-issues). `EventTagQuery` itself is already in `JasperFx.Events.Tags`. |

## Weasel.Core 9.0 — new canonical home

| Type | Old namespace(s) | New namespace | Status | Affects |
|---|---|---|---|---|
| Marten `IStorageOperation` family (`AppendEventOperationBase`, `QuickAppendEventsOperationBase`, `InsertStreamBase`, `UpdateStreamVersion`, `IncrementStreamVersionBy*`, `AssertStreamVersion*`, etc.) | `Marten` | `Weasel.Core` | 🔄 | Marten 9 + Weasel 9 (stated example in [JasperFx#214](https://github.com/JasperFx/jasperfx/issues/214) Rule 2) |
| Marten event-table / stream-table DDL (`EventsTable`, `StreamsTable`, etc.) | `Marten.Events.Schema` | partially shared with Weasel; Marten retains Postgres-specific bits | 🔄 | Marten 9 + Weasel 9 |

## Wolverine 6 — core promotion

| Type | Old namespace(s) | New namespace | Status | Affects |
|---|---|---|---|---|
| `AggregateHandlerAttribute`, `AggregateHandling`, `LoadAggregateFrame`, `MissingAggregateCheckFrame`, `RegisterEventsFrame`, `EventStoreFrame`, `TagAggregateOtelFrame`, `BoundaryEventCapture`, `ReadAggregateAttribute`, `WriteAggregateAttribute`, `BoundaryModelAttribute`, `ConsistentAggregateAttribute`, `ConsistentAggregateHandlerAttribute` | `Wolverine.Marten` / `Wolverine.Polecat` | `Wolverine.Persistence.EventSourcing` | 🔄 | Wolverine 6 |
| `IMartenOp` / `IPolecatOp` + 8 of 10 concrete ops (`StoreDoc<T>`, `StoreManyDocs<T>`, `InsertDoc<T>`, `UpdateDoc<T>`, `DeleteDoc<T>`, `DeleteDocWhere<T>`, `NoOp`) | `Wolverine.Marten` / `Wolverine.Polecat` | `Wolverine.Runtime.Persistence` (generic `Op<TSession,TDoc>` base) | 🔄 | Wolverine 6. Concrete factory wrappers (`MartenOps.X` / `PolecatOps.X`) may keep their old namespace with `[Obsolete]` aliases for one release. |
| `StoreObjects`, `DeleteDocById<T>`, `StartStream<T>` | `Wolverine.Marten` / `Wolverine.Polecat` | TBD — divergence-blocked | 🕓 | Wolverine 6. See deep-dive findings on Q9. |
| `IWolverineSubscription`, `BatchSubscription`, `WolverineSubscriptionRunner`, `ScopedWolverineSubscriptionRunner`, `InlineInvoker`, `InnerDataInvoker`, `NulloMessageInvoker`, `WolverineCallbackForCascadingMessages`, `PublishingRelay`, `EventRouter` | `Wolverine.Marten` / `Wolverine.Polecat` | `Wolverine.Persistence.EventSourcing` | 🔄 | Wolverine 6 |
| `EventStoreAgents`, `EventSubscriptionAgent`, `EventSubscriptionAgentFamily`, `WolverineProjectionCoordinator` | `Wolverine.Marten` / `Wolverine.Polecat` | `Wolverine.Persistence.EventSourcing` | 🔄 | Wolverine 6 |
| `UpdatedAggregate`, `UpdatedAggregate<T>` | `Wolverine.Marten` only | `Wolverine.Persistence.EventSourcing` (now available to Wolverine.Polecat too) | 🔄 | Wolverine 6 |
| `IMartenOutbox` / `IPolecatOutbox` (interfaces are character-for-character identical) | `Wolverine.Marten` / `Wolverine.Polecat` | `Wolverine.Persistence` (one unified `IDocumentSessionOutbox`) | 🔄 | Wolverine 6. Product-specific aliases retained for one release. |
| `OutboxedSessionFactory`, `OutboxedSessionFactoryGeneric`, `FlushOutgoingMessagesOnCommit`, `PublishIncomingEventsBeforeCommit` | `Wolverine.Marten` / `Wolverine.Polecat` | `Wolverine.Runtime.Persistence` — shared `OutboxedSessionFactory<TSession>` base | 🔄 | Wolverine 6 |
| `MartenToWolverineMessageBatch` (and Polecat equivalent once it exists) | `Wolverine.Marten` only today | `Wolverine.Runtime.Persistence` | 🕓 | Blocked on Polecat projection-emit-messages capability — see [open question 4](#open-questions) |

## Polecat follow-up issues

Several rows above require Polecat 4 to gain capability it does not have today.
These are tracked as separate Polecat issues so the JFx.Events 2.0 consumption
PR in Polecat can land in pieces:

1. **DCB parity with Marten (excluding HSTORE).** Polecat needs equivalents to Marten's `IEventBoundary<T>` + `FetchForWritingByTags<T>` + tag-table operations. Polecat already has `pc_event_tag_*` schema infrastructure; the query / operation surface is the gap. The Postgres-specific HSTORE storage mode stays Marten-side. Once this lands, `IEventBoundary<T>` and the DCB methods on `IEventStoreOperations` graduate to JFx.Events.
2. **`StoreObjects(IEnumerable<object>)` on `IDocumentOperations`.** Polecat's `IDocumentOperations` lacks the bulk-store API, forcing `Wolverine.Polecat.PolecatOps.StoreObjects` into per-document reflection dispatch. Adding the method collapses the op to Marten's one-liner and removes the reflection from the hot path.
3. **Tenant scoping for event-store operations.** Marten's `IMartenOp.StartStream<T>` wraps the session via `session.ForTenant(TenantId)` to scope event-stream operations to a tenant — but Polecat's `ITenantOperations` only exposes the read-only `IQueryEventStore`, so the equivalent wrap is impossible. Polecat needs an `ITenantOperations.Events` surface exposing the write side. (Marten's wrap is real code — confirmed by user.)
4. **Distributor layer.** Polecat needs equivalents to Marten's `SoloProjectionDistributor` / `SingleTenantProjectionDistributor` / `MultiTenantedProjectionDistributor` so Polecat can run multi-node coordinated daemons the same way Marten does. The JFx.Events 2.0 interfaces (`IProjectionDistributor`, `IProjectionSet`, `ProjectionLockIds`) give the seam; the storage-specific bits (SQL Server `sp_getapplock` for the lock implementation, database enumeration through Polecat's tenancy) are the Polecat-side work.
5. **`IMessageBatch` for projection-emitted messages.** Polecat 4 needs the equivalent of Marten's `MartenToWolverineMessageBatch` so projections can publish messages on commit. Without this, Wolverine's outbox is asymmetric between Marten and Polecat. Once it lands, the shared `OutboxedSessionFactory<TSession>` base in Wolverine 6 can absorb the projection-emit pathway.

## Marten clean-up that the migration should pick up

These are pre-existing items in Marten that are easier to fix during the relocation than as separate work:

* **`Marten.IReadOnlyEventStore`** — already in JFx.Events but with a different shape than Marten's `IQueryEventStore` (uses the generic `EventQuery` / `PagedEvents` instead of Marten's per-query overloads, and has no consumers in either product). Decide during Marten 9 whether to retire it, merge it into `IQueryEventStore`, or keep it as the generic event-query surface alongside.

## How to reference this table from a product migration guide

In each of Marten 9, Polecat 4, Wolverine 6 migration guides, add a section that says (e.g.):

```markdown
## Type relocations from the Critter Stack 2026 dedupe pillar

The shared substrate consolidation moved a number of types out of `Marten.*` /
`Polecat.*` / `Wolverine.*` and into `JasperFx.Events` / `Weasel.Core` /
`Wolverine.Persistence.*`. See the [canonical table][1] for the full list; the
rows below are the relocations that directly affect this product.

[1]: https://github.com/JasperFx/jasperfx/blob/main/docs/dedupe-namespace-table-2026.md

- `IEventStream<T>` is now `JasperFx.Events.IEventStream<T>`. Existing
  `Marten.Events.IEventStream<T>` / `Polecat.IEventStream<T>` remain as
  inheriting interfaces with no new members; user code that imports
  `using Marten.Events;` or `using Polecat;` keeps working.
- ... (per-product rows here)
```

The intent is for each product migration guide to call out the relocations its users will *see in their code*, not to restate the full table.
