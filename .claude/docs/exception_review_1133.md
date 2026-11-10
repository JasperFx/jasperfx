# Exception-type review: inventory and proposal

**Read from:** `jasperfx` origin/3.0, `marten` origin/10.0, `polecat` origin/6.0, `fisher` origin/2.0, `wolverine` origin/main (origin/3.0 for comparison). Fetched, not the local checkouts. Review only: no code changed, nothing reparented.

Counts on those branches (public/internal `*Exception` classes, tests and samples excluded): JasperFx ~40, Marten ~34, Polecat 7, Fisher 12.

**The state is better than the issue's starting table implies.** Polecat 6.0 and Fisher 2.0 already derive every shared name from the JasperFx type. Marten 10.0 is the outlier: for the shared names it derives from `MartenException`, so nothing has been rebased there yet.

## 1. Duplicate families

Legend: **D** = store type derives from the JasperFx type. **X** = duplicates it with no shared base. **N** = a different name for the same failure.

| # | Family | JasperFx (base) | Marten 10.0 (base) | Polecat 6.0 (base) | Fisher 2.0 (base) | Relation | Thrown / caught today | Proposal, and what breaks for a caller |
|---|---|---|---|---|---|---|---|---|
| 1 | `StreamLockedException` | `JasperFx.Events.StreamLockedException : Exception` | `: MartenException` | `: JasperFx.Events.StreamLockedException` | `: JasperFx.Events.StreamLockedException` | Marten **X**; Polecat, Fisher **D** | Marten: ~30 throw sites in `Events/Fetching/*` and `EventStore.ConcurrentAppends` (and, since marten#5818, a batched `FetchForExclusiveWriting`). Polecat: `EventOperations`. Fisher: `FisherSession`. Wolverine main has two separate 409 middlewares, `Wolverine.Http.Marten/ConcurrencyMapping.cs` and `Wolverine.Http.Polecat/ConcurrencyMapping.cs`, because the types share no base. | **Rebase Marten** on the JasperFx type (marten#5472, marten#5734). Breaks `catch (MartenException)` that currently catches it: it would stop matching unless `MartenException` stays in the chain. C# has no multiple inheritance, so it either drops `MartenException` (breaking) or the JasperFx type stays unreachable from Marten catches. See Q1. After the rebase, Wolverine collapses to one `catch (StreamLockedException)` mapping. |
| 2 | `ExistingStreamIdCollisionException` | `JasperFx.Events… : Exception` | `: MartenException` | `: JasperFx.Events…` | `: JasperFx.Events…` | Marten **X**; others **D** | Marten append path; Wolverine sample `WolverineWebApi/Marten/StreamCollisionExceptionPolicy.cs` catches the Marten type by name | **Rebase Marten** (marten#5471). Same `MartenException` trade-off as #1. A Wolverine policy keyed on the Marten type keeps working (still a subclass); one keyed on the JasperFx type starts matching Marten. |
| 3 | `NonExistentStreamException` | `JasperFx.Events… : Exception` | `: MartenException` | `: JasperFx.Events…` | `: JasperFx.Events…` | Marten **X**; others **D** | Marten `AppendOptimistic`/`AppendExclusive` on a missing stream | **Rebase Marten** (not yet filed; belongs with marten#5471/#5472). Same trade-off. |
| 4 | `UnknownEventTypeException` | `JasperFx.Events… : Exception, IEventFailureContext` | `: MartenException, IEventFailureContext` | `: JasperFx.Events…` | `: JasperFx.Events…` | Marten **X** | Event materialisation; surfaced by the daemon via `IEventFailureContext` (interface, so the daemon is unaffected) | **Rebase Marten** (not yet filed). The daemon reads the interface, so no catch is lost there. |
| 5 | `EventDeserializationFailureException` | `JasperFx.Events… : Exception, IEventFailureContext` | `: MartenException, IEventFailureContext` | `: JasperFx.Events…` | `: JasperFx.Events…` | Marten **X** | Same as #4 | **Rebase Marten** (not yet filed). |
| 6 | `BadLinqExpressionException` | `JasperFx… : Exception` | `: MartenException` | `: JasperFx.BadLinqExpressionException` | `: JasperFx.BadLinqExpressionException` | Marten **X** | LINQ parsing. `JasperFx.Documents.DocumentQueryCriteria.IsTranslationFailure` is the shared test in JasperFx | **Rebase Marten** (marten#5480). Check `IsTranslationFailure` once Marten derives, since it then also matches a Marten failure. |
| 7 | `ApplyEventException` | `JasperFx.Events.Daemon… : Exception, IEventFailureContext` | `: MartenException`, `[Obsolete]`, nothing throws it (#5337) | none | none | Marten **dead duplicate** | The daemon throws and catches the JasperFx one only: `GroupedProjectionExecution:397`, `ProjectionExecution:294`, `JasperFxAggregationProjectionBase:362/420`, and the source generator's emitted `catch (…ApplyEventException)`. | **Delete Marten's** in 10.0 (its own message already says so). Breaks only a `catch` that was already dead. |
| 8 | `DefaultTenantUsageDisabledException` | `JasperFx.Events… : Exception` | `: JasperFx.Events…` | `: JasperFx.Events…` | none | **D** everywhere | n/a | **Leave alone.** Done in marten#5514. This is the model the others should follow. |
| 9 | `ArchivedStreamException` | `JasperFx.Events… : Exception` | none. Appends to an archived stream throw generic `InvalidStreamOperationException : MartenException` (`RichEventAppender:144`, `QuickAppendEventsOperationBase:41`) | `: JasperFx.Events…`, thrown from `DocumentSessionBase` | `: JasperFx.Events…`, thrown from `AppendPlanner` | Marten **N** | Polecat kept `InvalidStreamException`, `[Obsolete]`, nothing throws it (#654). | **Marten: add `ArchivedStreamException`** and throw it (marten#5476). Breaks `catch (InvalidStreamOperationException)` for the archived case. Polecat's precedent: keep the old type obsolete. Consider making Marten's new type derive from both via a shim, see Q1. |
| 10 | `DisabledTenantException` | `: UnknownTenantIdException : Exception` | none (uses JasperFx `UnknownTenantIdException` directly) | none | `Fisher.…DisabledTenantException : JasperFx.MultiTenancy.DisabledTenantException`; `UnknownTenantException : UnknownTenantIdException` | Fisher **D** | Wolverine: `TenancyProblemMapping` (Http) tests `ex is DisabledTenantException`; `MultiTenantedMessageDatabase` catches `UnknownTenantException` | **Leave alone.** The hierarchy is deliberate and documented (a disabled tenant stays catchable as an unknown one). |
| 11 | `DuplicateNaturalKeyException` | `JasperFx.Events… : Exception` | not in Marten 10.0 | none | `: JasperFx.Events…` | Fisher **D** | n/a | **Leave alone.** |

## 2. Same failure under a different name, or a base that is missing

| Family | Types | Finding | Proposal |
|---|---|---|---|
| Optimistic concurrency, one root exists | `JasperFx.ConcurrencyException` ← `DcbConcurrencyException`, `EventStreamUnexpectedMaxEventIdException`. Wolverine's single `ConcurrencyExceptionMiddleware` (`Wolverine.Http/Runtime/ConflictMapping.cs`) handles all of them. | This part is healthy. | Leave alone. |
| **`StreamLockedException` is outside that family** | Not a `ConcurrencyException` in any store. Wolverine documents this in three files. | It is the same user-facing failure (409, retry) but a different base. | Decide whether JasperFx's `StreamLockedException` should derive from `ConcurrencyException` (Q2). If yes, the Marten/Polecat 409 middlewares and the extra `AddMiddleware` calls can be deleted. If no, keep it separate on purpose, and document why. Risk: code that does `catch (ConcurrencyException)` and retries would start catching lock timeouts. That is usually wanted but is a behaviour change. |
| `ConcurrentUpdateException` (Marten) | `: MartenException`; raised from `MartenExceptionTransformer` for a Postgres concurrent-update error | Same condition as `ConcurrencyException`, but not derived from it, so Wolverine's 409 mapping misses it. | Rebase on `JasperFx.ConcurrencyException` (new issue). Breaks `catch (MartenException)` the same way as #1. |
| `DocumentAlreadyExistsException` | Only JasperFx defines it; Marten (`RebuildBulkCopy`) and Polecat (`DocumentSessionBase`) throw it directly | No duplication. | Leave alone. |
| `ProgressionProgressOutOfOrderException` | JasperFx only, thrown by Marten's `UpdateProjectionProgress` | No duplication. | Leave alone. |
| `InvalidStreamOperationException` (Marten) / `InvalidStreamException` (Polecat) | Both generic "bad stream state" types | Polecat's is already dead. Marten's is still the catch-all for several unrelated conditions. | After #9, audit Marten's remaining throw sites. Candidate: map the "stream has no type under mandatory declaration" case to `StreamTypeMissingException`. |
| `MartenCommandException` | `: MartenException`; wraps Npgsql command failures. `Wolverine.Marten/MartenIntegration.cs:68` registers `OnException<MartenCommandException>`. | There is no store-neutral "database command failed" type, so Polecat and Fisher have no counterpart and Wolverine has a Marten-only policy. | Propose a JasperFx base (for example `StoreCommandException`) only if a Wolverine policy needs it for Polecat/Fisher. Otherwise leave it. This is a separate decision from the rest (Q3). |
| `MartenException` as a shared root | Every Marten type derives from it; Polecat and Fisher have no equivalent. | This is the cause of every "X" above: the store root blocks the shared root. | See Q1. |
| Tenant: `InvalidTenantForDatabaseException` (Marten) | `: MartenException` | No counterpart elsewhere. | Leave alone. |

## 3. Catch sites that would silently stop matching

| Site | Catch | Under which proposal |
|---|---|---|
| Wolverine `Wolverine.Http.Marten/ConcurrencyMapping.cs` and `Wolverine.Http.Polecat/ConcurrencyMapping.cs` | `StreamLockedException` (the store's type) | #1. Still matches a subclass, so nothing is lost. They become redundant and can go. |
| Wolverine `WolverineWebApi/Marten/StreamCollisionExceptionPolicy.cs` | Marten `ExistingStreamIdCollisionException` | #2. Still matches. |
| Wolverine `Wolverine.Marten/MartenIntegration.cs:68` | `OnException<MartenCommandException>` | Only if `MartenCommandException` is reparented. Not proposed. |
| Application `catch (MartenException)` | all Marten types | #1 to #6, if `MartenException` leaves the base chain. This is the main break. |
| JasperFx.Events daemon (`GroupedProjectionExecution`, `ProjectionExecution`, `JasperFxAggregationProjectionBase`, source-generated evolvers) | `ApplyEventException` (JasperFx type) | None. #7 only deletes a type that nothing throws. |
| `JasperFx.Documents.DocumentQueryCriteria.IsTranslationFailure` | `DynamicQueryException`, `NotSupportedException`, … | #6: re-check that a Marten `BadLinqExpressionException` now also matches. |
| Wolverine `MultiTenantedMessageDatabase`, `TenancyProblemMapping` | `UnknownTenantException`, `DisabledTenantException` | None. |

I found no `catch` in JasperFx.Events that targets a store's duplicate type; the daemon uses `IEventFailureContext` and the JasperFx types.

## 4. Proposal summary

1. **Rebase Marten's six shared-name types** (#1 to #6) onto the JasperFx ones in Marten 10. This finishes what Polecat and Fisher already did. Filed: marten#5471, #5472, #5476, #5480, #5734. **Not yet filed:** `NonExistentStreamException`, `UnknownEventTypeException`, `EventDeserializationFailureException`, and `ConcurrentUpdateException`.
2. **Delete** Marten's `ApplyEventException` (dead since #5337) and, in Polecat, `InvalidStreamException` (dead since #654).
3. **Leave alone:** #8, #10, #11, `DocumentAlreadyExistsException`, `ProgressionProgressOutOfOrderException`.
4. **Decide, don't assume:** the `MartenException` root (Q1), `StreamLockedException` under `ConcurrencyException` (Q2), a store-neutral command-failure base (Q3).

## 5. Open questions

- **Q1.** Marten types cannot derive from both `MartenException` and the JasperFx type. Options: (a) rebase and drop `MartenException` from the chain, accepting the break in 10.0, which is the point of the major; (b) keep the Marten types as they are and map both in Wolverine; (c) rebase only the types where `catch (MartenException)` is unlikely to be relied on. Recommended: (a) for #1 to #6, with a release note.
- **Q2.** Should `StreamLockedException` derive from `ConcurrencyException`? Recommended: yes in JasperFx 3.0.
- **Q3.** Do we want a store-neutral base for command failures? Recommended: not now.
