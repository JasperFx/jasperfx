# Parking a Failing Projection Identity

When an async projection throws on an event, the daemon has always had two choices: skip the event (write a dead letter and keep going) or pause the shard until somebody fixes it. Skipping loses the event for good, and pausing stops every other document the projection builds.

Parking is a third choice. When the slice of events for one identity fails, the daemon:

1. drops that identity's slice from the batch,
2. writes a parked row for the identity in the **same transaction** that moves the shard's progression,
3. keeps going with every other identity,
4. holds back every later event for the parked identity until it's replayed or discarded.

The parked identity's document stays at its last good state. Nothing is lost: the held events are still in the event store, and a replay folds them in.

## Turning it on

The default in JasperFx is unchanged. Each event store decides its own default, so check your store's documentation. To ask for parking explicitly:

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
| `Stream` | A multi-stream projection whose every rule is `IdentityFromStream<TEvent>()`. | Not yet, pauses |
| `Tag` | A multi-stream projection whose every rule is `IdentityFromTag<TTag>()` (or `IdentityFromStream`). | Not yet, pauses |
| `SliceIndex` | A multi-stream projection that keeps a slice index. | Not available yet |
| `ScanOnly` | A multi-stream projection grouped with `Identity`/`Identities` delegates, custom grouping, a custom slicer, or `RollUpByTenant()`. | No, pauses |
| `NotSupported` | Event projections, flat tables, subscriptions, and composite projections. | No, pauses |

Anything that can't park falls back to pausing the shard when the error action is `Park`, and the daemon logs once at startup why that projection doesn't park.

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

These rules are declared, never inferred. `Identity<OrderPlaced>(x => x.OrderId)` stays `ScanOnly` even when a `TagWith` rule tags the same value, because a delegate can't be compared to anything.

### Composite projections don't park

A composite projection does not park in this version. A parked upstream document would feed stale data to the stages after it, so a member that fails pauses the whole composite, or skips the event if the error action is `Skip`. `describe` shows composites as `NotSupported` with that reason.

## What fails and what parks

- An exception from your `Apply`/`Create`/`Evolve` code parks the identity.
- A systemic database failure (a missing column, a permissions problem) never parks. It pauses the shard, because every identity would fail the same way.
- An event the store can't deserialize parks its stream for a single-stream projection when `SerializationErrors` is `Park`.
- An exception from side effects (`RaiseSideEffects`) or enrichment falls back to the configured behavior.
- Inline projections never park: the error goes back to `SaveChanges` as before.
- A full rebuild never parks. It runs under the rebuild error options, and its teardown clears the projection's parked rows, because a rebuild re-folds every identity anyway.

## Guard rails

- **Cap:** a shard holds at most `MaxParkedIdentitiesPerShard` parked identities (1,000 by default, per tenant shard under tenant-partitioned events). A batch that would pass it doesn't commit, and the shard pauses with `ShardFailureCategory.ParkedLimitReached`.
- **Systemic trip:** if one batch would park more than `ParkedTripFraction` of its identities (half) and at least `ParkedTripMinimum` (20), the shard pauses with `ParkedLimitReached` instead. A bug that fails on every event of a new type stops on the first batch instead of parking a thousand identities.

A shard with parked identities reports them in `ShardState.ParkedCount`, `ISubscriptionAgent.ParkedCount` and the metrics `{prefix}.{projection}.{shard}.parked`, `.unparked`, `.parked.held_events` and `.parked.current`.

## Replay and discard

Replaying a parked identity is a per-identity rebuild in the shard that runs it. It re-folds the identity from nothing up to the next batch's ceiling and unparks it in that same batch. The side effects of the events above the point where it parked run once, in that batch, because those events never ran them before. If the fold fails again, the identity stays parked, and its row gets the new error and one more replay attempt.

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
