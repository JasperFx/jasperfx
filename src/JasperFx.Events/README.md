# JasperFx.Events

Foundational event store abstractions and projection infrastructure for the [Critter Stack](https://jasperfx.net). Consumed by **Marten** (Postgres) and **Polecat** (SQL Server) so a projection or async-daemon scenario written against `JasperFx.Events` works across both products.

Provides:

- **Projections** — `ProjectionBase`, `IInlineProjection`, snapshot / single-stream / multi-stream variants, and the `SnapshotLifecycle` enum.
- **Async daemon** — `AsyncShard`, `ISubscriptionExecution`, projection lifecycle and shard role semantics.
- **Slicing & grouping** — `SliceGroup`, `EventSlice`, `IEventSlicer` for fan-out projection topologies.
- **Event metadata** — `IEvent`, `IEventRegistry`, type registry primitives.
- **Descriptors** — `EventStoreUsage` and friends, surfaced through `IEventStoreUsageSource` to monitoring tools (CritterWatch).

## Quick start

```csharp
public class OrderProjection : SingleStreamProjection<Order, Guid>
{
    public Order Create(OrderPlaced e) => new(e.OrderId, e.Total);
    public Order Apply(OrderShipped e, Order state) => state with { Shipped = true };
}
```

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
