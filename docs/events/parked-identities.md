# Parking a Failing Projection Identity

When an async projection throws on an event, the daemon has always had two choices: skip the event (write a dead letter and keep going) or pause the shard until somebody fixes it. Skipping loses the event for good, and pausing stops every other document the projection builds.

Parking is a third choice. When the slice of events for one identity fails, the daemon:

1. drops that identity's slice from the batch,
2. writes a parked row for the identity in the **same transaction** that moves the shard's progression,
3. keeps going with every other identity,
4. holds back every later event for the parked identity until it's replayed or discarded.

The parked identity's document stays at its last good state. Nothing is lost: the held events are still in the event store, and a replay folds them in.

## Turning it on

JasperFx's own default is unchanged: a continuous run skips apply and serialization errors (`ProjectionGraph.Errors`), and a rebuild pauses on them (`ProjectionGraph.RebuildErrors`, and any `new ErrorHandlingOptions()`). Each event store sets its own default on top of that. Marten 10, Polecat 6 and Fisher 2 park a failing identity by default, and pause where they can't park. `RestoreV9Defaults()`, `RestoreV5Defaults()` and `RestoreV1Defaults()` put skipping back.

The in-memory prototyping store (`JasperFx.Events.InMemory`) pauses the shard on an apply or serialization error by default, so a prototype stops loudly. Change it through `Projections.Errors` the same as any other store, for example `ConfigureProjections(g => g.Errors.ApplyErrors = ProjectionErrorAction.Park)`. The store's old `ContinuousErrors` and `RebuildErrors` properties still work, but they're obsolete and are now just `Projections.Errors` and `Projections.RebuildErrors`.

To ask for parking explicitly, or with a store that doesn't park by default:

```cs
// Marten / Polecat / Fisher: StoreOptions.Projections.Errors is an ErrorHandlingOptions
opts.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;

// Optional: park the stream of an event the store can't deserialize too
opts.Projections.Errors.SerializationErrors = ProjectionErrorAction.Park;
```

`SkipApplyErrors` and `SkipSerializationErrors` still work. Setting one to `true` is `Skip`, `false` is `Pause`.

Parking needs a store that supports it (`IEventStore.SupportsParkedIdentities`). A store that doesn't treats `Park` as `Pause`.

## Which projections park

Only an identity whose events can be found again on their own can be parked and replayed. Every async projection gets an `IdentityAccess` value at startup, which `describe` shows along with the reason:

| Class | What it is | Parks? |
|---|---|---|
| `SingleStream` | A single-stream projection. An identity's events are its stream. | Yes |
| `Stream` | A multi-stream projection whose every rule is `IdentityFromStream<TEvent>()`. | Yes |
| `Tag` | A multi-stream projection whose every rule is `IdentityFromTag<TTag>()` (or `IdentityFromStream`). | Yes |
| `SliceIndex` | A multi-stream projection with `UseSliceIndex()`, whatever its grouping rules. | Yes |
| `ScanOnly` | A multi-stream projection grouped with `Identity`/`Identities` delegates, custom grouping, a custom slicer, grouping across tenants, or `RollUpByTenant()`, without the slice index. | No, pauses |
| `NotSupported` | Event projections, flat tables, subscriptions, and composite projections. | No, pauses |

Anything that can't park falls back to pausing the shard when the error action is `Park`, and the daemon logs once at startup why that projection doesn't park. So does a projection whose event store can't read its identities from that source (`IEventStore.SupportsIdentityEventSource`).

### Declaring the grouping by stream or by tag

For a multi-stream projection, declare the grouping so the daemon can find one identity's events again:

```cs
public class OrderSummaryProjection : MultiStreamProjection<OrderSummary, Guid>
{
    public OrderSummaryProjection()
    {
        // The identity is the stream the event was appended to
        IdentityFromStream<OrderShipped>();

        // The identity is the value of a stored DCB tag on the event
        IdentityFromTag<OrderId>();
    }
}
```

`IdentityFromTag<TTag>()` groups every event carrying a tag of a registered tag type by the tag's value. The projection's identity is the tag type itself or the simple value it wraps (`Guid` for `OrderId(Guid Value)`). An event tagged with two values of the type goes to both identities. The daemon reads the events' tags for a projection that declares this rule, which it doesn't do otherwise.

These rules are declared, never inferred. `Identity<OrderPlaced>(x => x.OrderId)` stays `ScanOnly` even when a `TagWith` rule tags the same value, because a delegate can't be compared to anything, and a tag can be added to an event by hand.

### The slice index

Any other multi-stream projection can opt into a slice index. The daemon records, in the batch that applies each event, which identities the event went to. One identity's events can then be found through the index, whatever the grouping rules are:

```cs
public class InvoiceTotalsProjection : MultiStreamProjection<InvoiceTotals, Guid>
{
    public InvoiceTotalsProjection()
    {
        Identities<LineItemsBilled>(x => x.InvoiceIds);

        // Rows are kept for 7 days, and for as long as a parked identity needs them
        Options.UseSliceIndex();
    }
}

// or when you register it
opts.Projections.Add<InvoiceTotalsProjection>(ProjectionLifecycle.Async, o => o.UseSliceIndex());
```

The index costs a row per (event, identity) pair and one more write per batch, which is why it's opt-in. Retention:

- `SliceIndexRetention.ForParking(window)`, the default with a window of 7 days. Rows older than the window are deleted on the shard's heartbeat (at most once per `PruneInterval`, one hour by default), except the rows a parked identity still needs. Enough to replay or discard a parked identity, not enough to rebuild any identity from the start.
- `SliceIndexRetention.Full` keeps every row. Use it when you want to rebuild one identity from the start or step through an identity's whole history.

```cs
Options.UseSliceIndex(SliceIndexRetention.Full);
Options.UseSliceIndex(SliceIndexRetention.ForParking(TimeSpan.FromDays(30)));
```

When you turn the index on for a projection that has already run, the index starts where the shard is. Parking works from then on, because every park after that point has its rows. Rebuilding one identity from nothing is refused with `IdentityRebuildRefusal.SliceIndexIncomplete` until the index reaches back to the start: rebuild the projection once, which writes the whole index again. A full rebuild always rewrites the index, a rewind deletes the rows above the new floor, and removing a tenant or a retired projection version removes its rows. If you turn the index off and on again later, rebuild the projection, because the index can't know what it missed in between.

### Custom grouping and the slice index

A custom grouping (an `IJasperFxAggregateGrouper` or a custom slicer) can look things up while it groups, and the answer can change after the event was applied. By default a replay uses the index **as recorded**: the identity gets exactly the events the index says it got, and the custom grouping doesn't run again. Fan-out rules that run after grouping still run.

If you'd rather run the grouping again over the indexed events and keep only what it gives the identity today, opt out:

```cs
Options.UseSliceIndex().RerunCustomGroupingOnReplay();
```

With the opt-out, an event the grouping now sends somewhere else is dropped from the replayed identity, and a replay doesn't write any other identity. Use it when your grouping is really a pure function of the events, or when today's answer is the one you want.

A custom grouping combined with a fan-out before grouping (`FanoutMode.BeforeGrouping`) stays `ScanOnly` even with the index, because the index records the parent event and only the grouping knows which child went where.

### Composite projections don't park

A composite projection does not park in this version. A parked upstream document would feed stale data to the stages after it, so a member that fails pauses the whole composite, or skips the event if the error action is `Skip`. `describe` shows composites as `NotSupported` with that reason.

## What fails and what parks

- An exception from your `Apply`/`Create`/`Evolve` code parks the identity.
- A systemic database failure (a missing column, a permissions problem) never parks. It pauses the shard, because every identity would fail the same way.
- An event the store can't deserialize parks its stream for a single-stream projection when `SerializationErrors` is `Park`. A multi-stream projection needs the event's body to know whose it is, so it falls back.
- An exception from side effects (`RaiseSideEffects`) or enrichment falls back to the configured behavior.
- Inline projections never park: the error goes back to `SaveChanges` as before.
- A full rebuild never parks. It runs under the rebuild error options, and its teardown clears the projection's parked rows, because a rebuild re-folds every identity anyway.
- A transient failure never parks either. See the next section.

## Transient failures

A connection pool timeout, a deadlock or a dropped connection isn't the event's fault, so it doesn't park, pause or skip anything at first. The daemon builds the whole batch again through the event store's `ResiliencePipeline`, the same Polly pipeline the store uses elsewhere, for as long as that pipeline retries. That's true under `Park`, `Pause` and `Skip`, for one failing identity or several at once, and for a failure outside your projection code, like loading the stored documents.

When the pipeline gives up, the shard pauses, and its `ShardFailure.IsTransient` is true, so Wolverine or CritterWatch can restart it later. With no pipeline (`ResiliencePipeline.Empty`, the default on `IEventStore`), that pause comes on the first failure.

What counts as transient is what `ProjectionExceptions.IsExceptionTransient` says, minus systemic database failures. A member of a composite projection isn't built again on its own, because it writes into its parent's batch.

## Guard rails

- **Cap:** a shard holds at most `MaxParkedIdentitiesPerShard` parked identities (1,000 by default, per tenant shard under tenant-partitioned events). A batch that would pass it doesn't commit, and the shard pauses with `ShardFailureCategory.ParkedLimitReached`.
- **Systemic trip:** if one batch would park more than `ParkedTripFraction` of its identities (half) and at least `ParkedTripMinimum` (20), the shard pauses with `ParkedLimitReached` instead. A bug that fails on every event of a new type stops on the first batch instead of parking a thousand identities.

A shard with parked identities reports them in `ShardState.ParkedCount`, `ISubscriptionAgent.ParkedCount` and the metrics `{prefix}.{projection}.{shard}.parked`, `.unparked`, `.parked.held_events` and `.parked.current`.

## Replay and discard

Replaying a parked identity is a per-identity rebuild in the shard that runs it, and it unparks the identity in the batch it binds to. A single-stream identity is re-folded from nothing up to that batch's ceiling. A multi-stream identity resumes from its stored document, which holds exactly its events up to the point where it parked, and folds its events since. The side effects of the events above the point where it parked run once, in that batch, because those events never ran them before. If the fold fails again, the identity stays parked, and its row gets the new error and one more replay attempt.

Discarding accepts the loss. It's a replay that drops and dead-letters each event that still fails, applies the rest, and unparks. It never loses more than skipping would have.

On the node that runs the shard:

```cs
var daemon = await store.BuildProjectionDaemonAsync();

var parked = await daemon.FetchParkedIdentitiesAsync("Trip", tenantId: null, token);

await daemon.ReplayParkedIdentityAsync("Trip", null, tripId, token);
await daemon.ReplayParkedIdentitiesAsync("Trip", null, ParkedSelection.All, token);
await daemon.ReplayParkedIdentitiesAsync("Trip", null, ParkedSelection.OldestFirst(5), token);
await daemon.DiscardParkedIdentityAsync("Trip", null, tripId, token);
```

From anywhere else, write a durable request. The node that runs the shard picks it up on its heartbeat:

```cs
var database = (await store.AllDatabases()).Single();
await database.RequestParkedActionAsync(new ParkedActionRequest
{
    ProjectionName = "Trip",
    Action = ParkedAction.Replay
}, token);
```

Nothing replays a parked identity outside the shard's own batch. If no node is running the shard, the request waits until the shard next starts.

Any successful per-identity rebuild of a parked identity unparks it too, whoever asked for it.

## Rebuilding one identity of a multi-stream projection

`RebuildIdentityAsync` works for a multi-stream projection whose identities can be found exactly. It re-folds the identity from nothing in the shard that runs it, the way it does for a single-stream projection:

```cs
await daemon.RebuildIdentityAsync("InvoiceTotals", tenantId: null, invoiceId, token);
```

A `ScanOnly` projection is refused with `IdentityRebuildRefusal.IdentityNotFindable`. For those there's a rebuild by scan, which reads every event of the projection's types, slices each page and keeps the identity's events. It's as expensive as it sounds, so it's logged as a warning and reports progress:

```cs
var progress = new Progress<IdentityScanProgress>(p =>
    Console.WriteLine($"{p.ScannedThrough} of {p.Ceiling}, {p.EventsFound} found"));

await daemon.RebuildIdentityByScanAsync("Departments", null, "engineering", progress, token);
```

## Reading one identity's events

`LoadIdentityEventsAsync` reads the events a projection gave one identity, a page at a time, from wherever that projection finds them. It's what a monitoring tool steps through:

```cs
var page = await store.LoadIdentityEventsAsync(database, new IdentityEventsRequest
{
    ProjectionName = "InvoiceTotals",
    Identity = invoiceId,
    AfterSequence = parked.ResumeAfter
}, token);

// page.Source is Stream, Tag, SliceIndex or Scan; page.Exact says whether it's every event in the range
var timeline = await store.RunMultiStreamProjectionForIdentityAsync("InvoiceTotals", invoiceId, records,
    startingState, token);
```

`RunMultiStreamProjectionForIdentityAsync` slices the events the way the daemon does, keeps the identity's slice and steps it from a starting state, so a parked identity can be stepped from its stored document straight to the event that fails. A `ScanOnly` projection needs a `ScanWindow` on the request, and its pages are never exact.

## Command line

```bash
dotnet run -- projections parked
dotnet run -- projections parked --projection Trip --replay --identity 3f2a...
dotnet run -- projections parked --projection Trip --replay --all
dotnet run -- projections parked --projection Trip --replay --oldest 5
dotnet run -- projections parked --projection Trip --discard --identity 3f2a... --yes
dotnet run -- projections parked --projection Trip --replay --all --wait 60
```

The command writes durable requests. It says so when no node is running the shard right now.

The listing shows one row per parked identity:

```
┌──────────┬────────────┬───────────┬──────────┬─────────────┬───────────┬──────────────────┬──────────────────────┬─────────┐
│ Database │ Projection │ Tenant    │ Identity │ Natural Key │ Failed At │ Error            │ Parked               │ Replays │
├──────────┼────────────┼───────────┼──────────┼─────────────┼───────────┼──────────────────┼──────────────────────┼─────────┤
│ main     │ Orders     │ *DEFAULT* │ order-1  │ ORD-1001    │        12 │ ApplyEvent: boom │ 2026-11-20 08:30:00Z │ 1       │
│ main     │ Orders     │ *DEFAULT* │ order-2  │             │        12 │ ApplyEvent: boom │ 2026-11-20 08:30:00Z │ 1       │
└──────────┴────────────┴───────────┴──────────┴─────────────┴───────────┴──────────────────┴──────────────────────┴─────────┘
```

`Natural Key` is the stream's natural key when the aggregate has one, and blank when it doesn't or the stream never got one. `--identity` still takes the stream id from the `Identity` column, not the natural key.

## For event store authors

A store that parks multi-stream identities implements, beyond what single-stream parking needs:

- `IEventStore.SupportsIdentityEventSource(source)` and `LoadEventsForIdentityAsync(database, IdentityEventLoadRequest, token)` for each source it reads: `Stream` (the default does this through `LoadEventsForIdentityRebuildAsync`), `Tag` (events carrying a tag of `TagType` whose value is `IdentityValue`, with their tags), `SliceIndex` (the events whose sequences the index lists for the identity, whatever tenant the event is in) and `Scan` (every event of the projection's types in the range).
- Reading the events' tags in the daemon's loader for a projection with `IncludeEventTags`, so `IdentityFromTag` groups anything in an async projection.
- For the slice index: `IProjectionBatch.AppendSliceIndex` in the batch's transaction, `IEventDatabase.PruneSliceIndexAsync`, the event-range marks (the index's start is the mark under `SliceIndexMarks.KeyFor(...)`), and deleting the index's rows with the projection's rebuild teardown, a rewind (rows above the new floor), a tenant's removal and a retired version's eject. One index per projection name and version, keyed by (tenant, identity, sequence).

The `MultiStreamParkedIdentityCompliance` suite checks all of it. The in-memory store in `JasperFx.Events.InMemory` is the reference implementation.

A store that supports natural keys fills `ParkedIdentity.NaturalKey` in `IEventDatabase.FetchParkedIdentitiesAsync`, with one natural-key lookup per projection. It's never stored on the parked row.

To have transient batch failures retried, return your store's Polly pipeline from `IEventStore<TOperations, TQuerySession>.ResiliencePipeline`. The daemon only hands it failures it already judged transient, and an `AggregateException` from several identities failing at once is handed over as its first inner exception, so a `ShouldHandle` written against your driver's exceptions works as it is.
